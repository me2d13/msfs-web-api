using System;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using MsfsApiServer.Logging;
using MsfsApiServer.Configuration;
using MsfsApiServer.Udp;
using SimConnector;

class Program
{
    public static void Main(string[] args)
    {
        // Load configuration with precedence: defaults < config file < command-line args
        var configResult = ConfigLoader.Load(args);
        var config = configResult.Config;

        // Create and configure the app builder
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddCommandLine(args);

        // Configure logging from config
        string logFilePath = ResolveLogFilePath(config.General.LogFile);
        var logLevel = ParseLogLevel(config.General.LogLevel);

        // Override the configuration-based log level (overrides appsettings.json)
        builder.Configuration["Logging:LogLevel:Default"] = logLevel.ToString();

        // Apply global minimum log level so factory does not filter out Debug/Trace when requested
        builder.Logging.SetMinimumLevel(logLevel);
        builder.Services.Configure<LoggerFilterOptions>(o => o.MinLevel = logLevel);

        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            try
            {
                var dir = Path.GetDirectoryName(logFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // Recreate on start (truncate), keep after app exits
                using (File.Create(logFilePath)) { }

                // Route all ILogger<T> to file (and Debug for dev); ignore failures silently
                builder.Logging.ClearProviders();
                builder.Logging.AddProvider(new SimpleFileLoggerProvider(logFilePath, logLevel));
                builder.Logging.AddDebug();
                // Ensure min level still applied after provider changes
                builder.Logging.SetMinimumLevel(logLevel);
                builder.Services.Configure<LoggerFilterOptions>(o => o.MinLevel = logLevel);
            }
            catch
            {
                // Silently ignore logging setup errors (permissions, path issues)
            }
        }

        // Inject resolved port from config into builder configuration for WebApiHost
        var port = config.WebApi.Port?.ToString() ?? "5018";
        builder.Configuration["port"] = port;

        // Start the web API host and get port, local IP, and the built app
        var (actualPort, localIp, app) = WebApiHost.StartAndReturnApp(builder);

        // Now that logging is initialized, log the configuration diagnostics
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Configuration: {ConfigSummary}", configResult.Diagnostics.GetSummary());
        logger.LogInformation("Log level: {LogLevel}", logLevel);
        logger.LogInformation("Debug enabled: {Enabled}", logger.IsEnabled(LogLevel.Debug));
        logger.LogDebug("Debug logging confirmed active at level {Level}", logLevel);

        // Start UDP streaming if enabled in config
        UdpStreamingService? udpService = null;
        if (config.Udp.Enabled)
        {
            try
            {
                var simVarService = app.Services.GetRequiredService<SimVarService>();
                var udpLogger = app.Services.GetRequiredService<ILogger<UdpStreamingService>>();
                udpService = new UdpStreamingService(simVarService, config.Udp, udpLogger);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to start UDP streaming");
                // Continue without UDP streaming if it fails
            }
        }

        // Hook up SimConnect events to TrayIconManager
        var simConnectClient = app.Services.GetRequiredService<SimConnectClient>();
        simConnectClient.OnConnected += () => TrayIconManager.SetSimConnectStatus(true);
        simConnectClient.OnDisconnected += () => TrayIconManager.SetSimConnectStatus(false);
        // Initial status check (in case it connected very fast)
        TrayIconManager.SetSimConnectStatus(simConnectClient.IsConnected);

        // Start the tray icon UI
        TrayIconManager.Start(actualPort, localIp);

        // Keep main thread alive
        Thread.Sleep(Timeout.Infinite);
    }

    private static string ResolveLogFilePath(string? logFileArg)
    {
        if (!string.IsNullOrWhiteSpace(logFileArg)) return logFileArg!;
        try
        {
            var exe = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(exe))
            {
                var dir = Path.GetDirectoryName(exe)!;
                var name = Path.GetFileNameWithoutExtension(exe);
                return Path.Combine(dir, name + ".log");
            }
        }
        catch { }
        return string.Empty;
    }

    private static LogLevel ParseLogLevel(string? logLevelStr)
    {
        if (string.IsNullOrWhiteSpace(logLevelStr))
            return LogLevel.Information; // Default

        // Parse log level string (case-insensitive)
        if (Enum.TryParse<LogLevel>(logLevelStr, ignoreCase: true, out var level))
        {
            return level;
        }

        // Invalid log level, default to Information and warn (but we can't log yet!)
        Console.WriteLine($"Warning: Invalid log level '{logLevelStr}', defaulting to Information. Valid values: Trace, Debug, Information, Warning, Error, Critical, None");
        return LogLevel.Information;
    }
}


/*
Example usage with curl:

Single variable operations:
curl -X 'POST' 'http://localhost:5018/api/simvar/get' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"simVarName": "L:VC_AT_ARM_LIGHT_VAL"}'
curl -X 'POST' 'http://localhost:5018/api/simvar/get' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"simVarName": "CAMERA VIEW TYPE AND INDEX:1"}'
curl -X 'POST' 'http://localhost:5018/api/simvar/get' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"simVarName": "FLAPS HANDLE INDEX","unit": "Number"}'
curl -X 'POST' 'http://localhost:5018/api/simvar/get' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"simVarName": "GENERAL ENG THROTTLE LEVER POSITION:1","unit": "Percent"}'
curl -X 'POST' 'http://localhost:5018/api/simvar/get' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"simVarName": "CAMERA VIEW TYPE AND INDEX:0","unit": ""}'

curl -X 'POST' 'http://localhost:5018/api/simvar/set' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"simVarName": "CAMERA STATE", "value": 3}'
curl -X 'POST' 'http://localhost:5018/api/simvar/set' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"simVarName": "FLAPS HANDLE INDEX", "value": 1}'
curl -X 'POST' 'http://localhost:5018/api/simvar/set' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"simVarName": "CAMERA VIEW TYPE AND INDEX:1", "value": 2}'

Multiple variable operations:
curl -X 'POST' 'http://localhost:5018/api/simvar/getMultiple' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '[{"simVarName": "CAMERA STATE","unit": ""}, {"simVarName": "FLAPS HANDLE INDEX","unit": "Number"}]'
curl -X 'POST' 'http://localhost:5018/api/simvar/getMultiple' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '[{"simVarName": "CAMERA STATE"}, {"simVarName": "CAMERA VIEW TYPE AND INDEX:0"}, {"simVarName": "CAMERA VIEW TYPE AND INDEX:1"}]'
// Switch to cockpit and set flaps to 2
curl -X 'POST' 'http://localhost:5018/api/simvar/setMultiple' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '[{"simVarName": "CAMERA STATE", "value": 2}, {"simVarName": "FLAPS HANDLE INDEX", "unit": "Number", "value": 2}]'
cockipt instrument view 3:
curl -X 'POST' 'http://localhost:5018/api/simvar/setMultiple' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '[{"simVarName": "CAMERA STATE", "value": 2}, {"simVarName": "CAMERA VIEW TYPE AND INDEX:1", "value": 2}]'
curl -X 'POST' 'http://localhost:5018/api/simvar/setMultiple' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '[{"simVarName": "CAMERA STATE", "value": 2}, {"simVarName": "CAMERA VIEW TYPE AND INDEX:0", "value": 2}, {"simVarName": "CAMERA VIEW TYPE AND INDEX:1", "value": 2}]'


Events:
curl -X 'POST' 'http://localhost:5018/api/event/send' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"name": "TAXI_LIGHTS_SET", "value": 1}'
curl -X 'POST' 'http://localhost:5018/api/event/send' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"name": "PARKING_BRAKES"}'

cd /z/home/p/msfsapi/src/MsfsApiServer/bin/x64/Release/net8.0-windows7.0/
./MsfsApiServer.exe --config ../../../../../config-udp-test.yaml
*/
