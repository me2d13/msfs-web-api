using System;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging;
using MsfsApiServer.Logging;

class Program
{
    public static void Main(string[] args)
    {
        // Create and configure the app builder first so logging is app-wide
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddCommandLine(args);

        // Resolve log file path: --logFile can override, else next to EXE with .log
        string? logFileArg = builder.Configuration["logFile"]; // usage: --logFile "C:\\path\\app.log"
        string logFilePath = ResolveLogFilePath(logFileArg);
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
                builder.Logging.AddProvider(new SimpleFileLoggerProvider(logFilePath));
                builder.Logging.AddDebug();
            }
            catch
            {
                // Silently ignore logging setup errors (permissions, path issues)
            }
        }

        // Start the web API host and get port and local IP
        var (port, localIp) = WebApiHost.Start(builder);

        // Start the tray icon UI
        TrayIconManager.Start(port, localIp);

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
}


/*
Example usage with curl:

Single variable operations:
curl -X 'POST' 'http://localhost:5018/api/simvar/get' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"simVarName": "CAMERA STATE"}'
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
curl -X 'POST' 'http://localhost:5018/api/event/send' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"name": "AXIS_PAN_HEADING", "value": 90}'
curl -X 'POST' 'http://localhost:5018/api/event/send' -H 'accept: text/plain' -H 'Content-Type: application/json' -d '{"name": "EYEPOINT_RESET"}'
*/
