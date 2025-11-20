using Microsoft.Extensions.Logging;
using MsfsApiServer.Configuration;
using SimConnector;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MsfsApiServer.Udp
{
    /// <summary>
    /// Streams SimVar values via UDP at configured intervals.
    /// Polls variables from SimConnect and sends as JSON map {varName: value}.
    /// Handles target unavailability with automatic reconnection.
    /// </summary>
    public class UdpStreamingService : IDisposable
    {
        private readonly ILogger<UdpStreamingService> _logger;
        private readonly SimVarService _simVarService;
        private readonly UdpConfig _config;
        private UdpClient? _udpClient;
        private readonly System.Threading.Timer _timer;
        private readonly IPEndPoint _targetEndpoint;
        private readonly List<SimVarReference> _simVars;
        private bool _disposed;

        // Connection state management
        private bool _isConnected = true;
        private DateTime _lastConnectionAttempt = DateTime.MinValue;
        private const int RECONNECT_INTERVAL_SECONDS = 5;
        private int _consecutiveFailures = 0;
        private const int MAX_FAILURES_BEFORE_RECONNECT = 3;

        public UdpStreamingService(
            SimVarService simVarService,
            UdpConfig config,
            ILogger<UdpStreamingService> logger)
        {
            _logger = logger;
            _simVarService = simVarService;
            _config = config;

            // Validate configuration
            if (string.IsNullOrWhiteSpace(config.TargetHost) || !config.TargetPort.HasValue)
            {
                throw new ArgumentException("UDP config must have targetHost and targetPort");
            }

            if (config.Variables == null || config.Variables.Count == 0)
            {
                throw new ArgumentException("UDP config must have at least one variable");
            }

            // Resolve target endpoint (supports both IP addresses and hostnames)
            _targetEndpoint = ResolveEndpoint(config.TargetHost, config.TargetPort.Value);

            // Initialize UDP client
            InitializeUdpClient();

            // Build SimVarReference list from config, parsing aliases
            // Variables can be in format "VAR_NAME" or "VAR_NAME|OUTPUT_ALIAS"
            _simVars = config.Variables
                .Select(varString => SimVarParser.CreateReference(varString, unit: ""))
                .ToList();

            // Start timer with configured interval (default 1000ms)
            var intervalMs = config.Interval ?? 1000;
            _timer = new System.Threading.Timer(
                 async _ => await SendUpdateAsync(),
                 null,
                  TimeSpan.FromMilliseconds(intervalMs),
                       TimeSpan.FromMilliseconds(intervalMs)
             );

            _logger.LogInformation(
     "UDP streaming started: {Count} variables to {Host}:{Port} every {Interval}ms",
         _simVars.Count,
                config.TargetHost,
             config.TargetPort,
            intervalMs
         );
        }

        /// <summary>
        /// Resolve hostname or IP address to IPEndPoint.
        /// Supports both IP addresses (e.g., "192.168.1.100") and hostnames (e.g., "localhost").
        /// </summary>
        private IPEndPoint ResolveEndpoint(string host, int port)
        {
            try
            {
                // Try to parse as IP address first
                if (IPAddress.TryParse(host, out var ipAddress))
                {
                    return new IPEndPoint(ipAddress, port);
                }

                // If not an IP, resolve as hostname via DNS
                var addresses = Dns.GetHostAddresses(host);
                if (addresses.Length == 0)
                {
                    throw new ArgumentException($"Could not resolve hostname '{host}' to an IP address");
                }

                // Prefer IPv4 addresses
                var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 != null)
                {
                    _logger.LogInformation("Resolved hostname '{Host}' to IPv4 address {IP}", host, ipv4);
                    return new IPEndPoint(ipv4, port);
                }

                // Fallback to first available address (might be IPv6)
                _logger.LogInformation("Resolved hostname '{Host}' to address {IP}", host, addresses[0]);
                return new IPEndPoint(addresses[0], port);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Failed to resolve target host '{host}': {ex.Message}", ex);
            }
        }

        private void InitializeUdpClient()
        {
            try
            {
                _udpClient?.Dispose();
                _udpClient = new UdpClient();
                _isConnected = true;
                _consecutiveFailures = 0;
                _logger.LogInformation("UDP client initialized for {Host}:{Port}", _config.TargetHost, _config.TargetPort);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize UDP client");
                _isConnected = false;
            }
        }

        private async Task SendUpdateAsync()
        {
            if (_disposed) return;

            // Check if we need to attempt reconnection
            if (!_isConnected)
            {
                var timeSinceLastAttempt = DateTime.UtcNow - _lastConnectionAttempt;
                if (timeSinceLastAttempt.TotalSeconds < RECONNECT_INTERVAL_SECONDS)
                {
                    // Too soon to retry, skip this cycle
                    return;
                }

                // Attempt to reinitialize
                _logger.LogInformation("Attempting to reconnect UDP client...");
                _lastConnectionAttempt = DateTime.UtcNow;
                InitializeUdpClient();

                if (!_isConnected)
                {
                    _logger.LogWarning("UDP reconnection failed, will retry in {Seconds}s", RECONNECT_INTERVAL_SECONDS);
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
                    _logger.LogDebug("No SimVar results available, skipping UDP send");
                    return;
                }

                // Filter out NaN values (timeouts or errors)
                // Only include variables with valid numeric values
                var validResults = results.Where(r => !double.IsNaN(r.Value) && !double.IsInfinity(r.Value)).ToList();

                if (validResults.Count == 0)
                {
                    _logger.LogDebug("All SimVar values are invalid (NaN/Infinity), skipping UDP send");
                    return;
                }

                // Build JSON map using output names (alias if present, otherwise var name)
                var data = validResults.ToDictionary(
                    r => r.GetOutputName(),
                    r => r.Value
                );

                var json = JsonSerializer.Serialize(data);
                var bytes = Encoding.UTF8.GetBytes(json);

                // Send via UDP
                if (_udpClient != null)
                {
                    await _udpClient.SendAsync(bytes, bytes.Length, _targetEndpoint);
                    _consecutiveFailures = 0; // Reset failure counter on success
                }
            }
            catch (SocketException ex)
            {
                _consecutiveFailures++;
                _logger.LogWarning(ex, "UDP send failed (attempt {Count}/{Max}): {Message}",
                   _consecutiveFailures, MAX_FAILURES_BEFORE_RECONNECT, ex.Message);

                // If we've had multiple consecutive failures, mark as disconnected and reinit
                if (_consecutiveFailures >= MAX_FAILURES_BEFORE_RECONNECT)
                {
                    _logger.LogWarning("Too many consecutive failures, marking UDP as disconnected");
                    _isConnected = false;
                    _lastConnectionAttempt = DateTime.UtcNow;
                }
            }
            catch (ObjectDisposedException)
            {
                // UDP client was disposed, mark as disconnected
                _logger.LogWarning("UDP client disposed, marking as disconnected");
                _isConnected = false;
                _lastConnectionAttempt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogWarning(ex, "Error sending UDP update (attempt {Count}/{Max})",
                   _consecutiveFailures, MAX_FAILURES_BEFORE_RECONNECT);

                if (_consecutiveFailures >= MAX_FAILURES_BEFORE_RECONNECT)
                {
                    _isConnected = false;
                    _lastConnectionAttempt = DateTime.UtcNow;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _timer?.Dispose();
            _udpClient?.Dispose();

            _logger.LogInformation("UDP streaming stopped");
        }
    }
}
