using Microsoft.Extensions.Logging;
using MsfsApiServer.Configuration;
using SimConnector;
using System.IO.Ports;
using System.Text;
using System.Text.Json;

namespace MsfsApiServer.Serial
{
    /// <summary>
    /// Streams SimVar values via a serial port at configured intervals.
    /// Polls variables from SimConnect and sends as a JSON line {varName: value}\n.
    /// Handles port errors with automatic reconnection.
    /// </summary>
    public class SerialStreamingService : IDisposable
    {
        private readonly ILogger<SerialStreamingService> _logger;
        private readonly SimVarService _simVarService;
        private readonly SerialConfig _config;
        private SerialPort? _serialPort;
        private readonly System.Threading.Timer _timer;
        private readonly List<SimVarReference> _simVars;
        private bool _disposed;

        // Connection state management
        private bool _isConnected = false;
        private DateTime _lastConnectionAttempt = DateTime.MinValue;
        private const int RECONNECT_INTERVAL_SECONDS = 5;
        private int _consecutiveFailures = 0;
        private const int MAX_FAILURES_BEFORE_RECONNECT = 3;

        public SerialStreamingService(
            SimVarService simVarService,
            SerialConfig config,
            ILogger<SerialStreamingService> logger)
        {
            _logger = logger;
            _simVarService = simVarService;
            _config = config;

            // Validate configuration
            if (string.IsNullOrWhiteSpace(config.ComPort))
            {
                throw new ArgumentException("Serial config must have a comPort specified");
            }

            if (config.Variables == null || config.Variables.Count == 0)
            {
                throw new ArgumentException("Serial config must have at least one variable");
            }

            // Build SimVarReference list from config, parsing aliases
            // Variables can be in format "VAR_NAME" or "VAR_NAME|OUTPUT_ALIAS"
            _simVars = config.Variables
                .Select(varString => SimVarParser.CreateReference(varString, unit: ""))
                .ToList();

            // Open the serial port
            InitializeSerialPort();

            // Start timer with configured interval (default 1000ms)
            var intervalMs = config.Interval ?? 1000;
            _timer = new System.Threading.Timer(
                async _ => await SendUpdateAsync(),
                null,
                TimeSpan.FromMilliseconds(intervalMs),
                TimeSpan.FromMilliseconds(intervalMs)
            );

            _logger.LogInformation(
                "Serial streaming started: {Count} variables to {Port} at {BaudRate} baud every {Interval}ms",
                _simVars.Count,
                config.ComPort,
                config.BaudRate,
                intervalMs
            );
        }

        private void InitializeSerialPort()
        {
            try
            {
                _serialPort?.Dispose();
                _serialPort = null;

                var port = new SerialPort(_config.ComPort!)
                {
                    BaudRate  = _config.BaudRate,
                    DataBits  = _config.DataBits,
                    Parity    = ParseEnum<Parity>(_config.Parity, System.IO.Ports.Parity.None),
                    StopBits  = ParseEnum<StopBits>(_config.StopBits, System.IO.Ports.StopBits.One),
                    Handshake = ParseEnum<Handshake>(_config.Handshake, System.IO.Ports.Handshake.None),
                    Encoding  = Encoding.UTF8,
                    NewLine   = "\n",
                    WriteTimeout = 2000,
                };

                port.Open();
                _serialPort = port;
                _isConnected = true;
                _consecutiveFailures = 0;

                _logger.LogInformation(
                    "Serial port {Port} opened ({BaudRate},{DataBits},{Parity},{StopBits},{Handshake})",
                    _config.ComPort, _config.BaudRate, _config.DataBits,
                    _config.Parity, _config.StopBits, _config.Handshake);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to open serial port {Port}", _config.ComPort);
                _isConnected = false;
                _lastConnectionAttempt = DateTime.UtcNow;
            }
        }

        private async Task SendUpdateAsync()
        {
            if (_disposed) return;

            // Attempt reconnection if port is not open
            if (!_isConnected)
            {
                var timeSinceLastAttempt = DateTime.UtcNow - _lastConnectionAttempt;
                if (timeSinceLastAttempt.TotalSeconds < RECONNECT_INTERVAL_SECONDS)
                {
                    return; // Too soon to retry
                }

                _logger.LogInformation("Attempting to reopen serial port {Port}...", _config.ComPort);
                _lastConnectionAttempt = DateTime.UtcNow;
                InitializeSerialPort();

                if (!_isConnected)
                {
                    _logger.LogWarning("Serial reconnection failed, will retry in {Seconds}s", RECONNECT_INTERVAL_SECONDS);
                    return;
                }
            }

            try
            {
                // Get current values from SimConnect
                var results = await _simVarService.GetMultipleSimVarValuesAsync(_simVars);

                // Skip sending if no valid results (SimConnect not connected)
                if (results == null || results.Count == 0)
                {
                    _logger.LogDebug("No SimVar results available, skipping serial send");
                    return;
                }

                // Filter out NaN / Infinity values
                var validResults = results
                    .Where(r => !double.IsNaN(r.Value) && !double.IsInfinity(r.Value))
                    .ToList();

                if (validResults.Count == 0)
                {
                    _logger.LogDebug("All SimVar values are invalid (NaN/Infinity), skipping serial send");
                    return;
                }

                // Build JSON map using output names (alias if present, otherwise var name)
                var data = validResults.ToDictionary(
                    r => r.GetOutputName(),
                    r => r.Value
                );

                var json = JsonSerializer.Serialize(data);

                // Write JSON line to the serial port
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    _serialPort.WriteLine(json);
                    _consecutiveFailures = 0; // Reset failure counter on success
                }
            }
            catch (TimeoutException ex)
            {
                HandleSendFailure(ex, "Write timeout on serial port {Port}");
            }
            catch (InvalidOperationException ex)
            {
                // Port was closed unexpectedly
                HandleSendFailure(ex, "Serial port {Port} was closed unexpectedly");
            }
            catch (Exception ex)
            {
                HandleSendFailure(ex, "Error writing to serial port {Port}");
            }
        }

        private void HandleSendFailure(Exception ex, string messageTemplate)
        {
            _consecutiveFailures++;
            _logger.LogWarning(ex, messageTemplate + " (attempt {Count}/{Max})",
                _config.ComPort, _consecutiveFailures, MAX_FAILURES_BEFORE_RECONNECT);

            if (_consecutiveFailures >= MAX_FAILURES_BEFORE_RECONNECT)
            {
                _logger.LogWarning("Too many consecutive failures, closing serial port {Port} to reconnect", _config.ComPort);
                _isConnected = false;
                _lastConnectionAttempt = DateTime.UtcNow;
                try { _serialPort?.Close(); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Parse a string into a .NET enum, falling back to a default if unrecognised.
        /// </summary>
        private static T ParseEnum<T>(string value, T defaultValue) where T : struct, Enum
        {
            if (Enum.TryParse<T>(value, ignoreCase: true, out var result))
                return result;

            return defaultValue;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _timer?.Dispose();

            try
            {
                if (_serialPort?.IsOpen == true)
                    _serialPort.Close();
            }
            catch { /* ignore on shutdown */ }

            _serialPort?.Dispose();

            _logger.LogInformation("Serial streaming stopped ({Port})", _config.ComPort);
        }
    }
}
