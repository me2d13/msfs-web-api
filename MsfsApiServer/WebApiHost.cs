using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimConnector;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text;

public static class WebApiHost
{
    // New overload that accepts a pre-configured builder and returns the app (for service access)
    public static (string port, string localIp, WebApplication app) StartAndReturnApp(WebApplicationBuilder builder)
    {
        // Ensure command line args are already added by caller if needed
        var port = builder.Configuration["port"] ?? "5018";
        var url = $"http://0.0.0.0:{port}";
        builder.WebHost.UseUrls(url);

        builder.Services.AddSingleton<SimConnectClient>();
        builder.Services.AddSingleton<SimVarService>();
        builder.Services.AddSingleton<SimEventService>();
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
     {
            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
        });
        });
        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        var app = builder.Build();
        app.UseCors();
        app.UseWebSockets();
        app.UseSwagger();
        app.UseSwaggerUI();

        string localIp = "0.0.0.0";
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            localIp = host.AddressList.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString() ?? "0.0.0.0";
        }
        catch { }

        app.MapGet("/api/status", (SimConnectClient simClient) =>
        {
            simClient.RefreshConnection();
            string connectionStatus = simClient.IsConnected ? "CONNECTED" : "DISCONNECTED";
            return $"API Server Running. MSFS Connection Status: {connectionStatus}";
        });
        app.MapPost("/api/simvar/get", async (SimVarService simVarService, SimVarReference reference) =>
        {
            var result = await simVarService.GetSimVarValueAsync(reference);
            return Results.Json(result);
        });
        app.MapPost("/api/simvar/set", async (SimVarService simVarService, SimVarReference reference) =>
        {
            var result = await simVarService.SetSimVarValueAsync(reference);
            return Results.Json(result);
        });
        app.MapPost("/api/simvar/getMultiple", async (SimVarService simVarService, List<SimVarReference> references) =>
        {
            var results = await simVarService.GetMultipleSimVarValuesAsync(references);
            return Results.Json(results);
        });
        app.MapPost("/api/simvar/setMultiple", async (SimVarService simVarService, List<SimVarReference> references) =>
        {
            var results = await simVarService.SetMultipleSimVarValuesAsync(references);
            return Results.Json(results);
        });
        app.MapPost("/api/event/send", (SimEventService simEventService, EventReference reference) =>
        {
            simEventService.SendEvent(reference);
            return Results.Ok("OK");
        });

        // WebSocket endpoint for simvar updates
        app.Map("/api/simvar/register", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode =400;
                await context.Response.WriteAsync("WebSocket connection expected.");
                return;
            }

            // Get a logger for this request
            var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
            var wsLogger = loggerFactory.CreateLogger("SimVarWebSocket");

            // Query params
            // interval: seconds between updates (default2)
            int intervalSec =2;
            if (context.Request.Query.TryGetValue("interval", out var intervalStr) && int.TryParse(intervalStr, out var parsed))
                intervalSec = parsed;
            // alwaysUpdate: when true, send every interval even if values didn't change (default false)
            bool alwaysUpdate = false;
            if (context.Request.Query.TryGetValue("alwaysUpdate", out var alwaysStr) && bool.TryParse(alwaysStr, out var alwaysParsed))
                alwaysUpdate = alwaysParsed;

            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            wsLogger.LogInformation("WS /api/simvar/register opened from {RemoteIp} interval={Interval}s alwaysUpdate={Always}", context.Connection.RemoteIpAddress, intervalSec, alwaysUpdate);

            // Receive the first message from the client (should be the SimVarReference list as JSON)
            var buffer = new byte[4096];
            WebSocketReceiveResult result = null;
            try
            {
                result = await webSocket.ReceiveAsync(buffer, context.RequestAborted);
            }
            catch
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Error receiving message", context.RequestAborted);
                wsLogger.LogInformation("WS closed (receive error) from {RemoteIp}", context.Connection.RemoteIpAddress);
                return;
            }
            if (result == null)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "No message received", context.RequestAborted);
                wsLogger.LogInformation("WS closed (no first message) from {RemoteIp}", context.Connection.RemoteIpAddress);
                return;
            }
            if (result.MessageType != WebSocketMessageType.Text)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Expected text message", context.RequestAborted);
                wsLogger.LogInformation("WS closed (non-text first message) from {RemoteIp}", context.Connection.RemoteIpAddress);
                return;
            }
            var jsonString = Encoding.UTF8.GetString(buffer,0, result.Count);
            List<SimVarReference> simVars;
            try
            {
                simVars = JsonSerializer.Deserialize<List<SimVarReference>>(jsonString, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (simVars == null || simVars.Count ==0)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "No sim variables provided.", context.RequestAborted);
                    wsLogger.LogInformation("WS closed (no variables) from {RemoteIp}", context.Connection.RemoteIpAddress);
                    return;
                }
                wsLogger.LogInformation("WS registered {Count} vars from {RemoteIp}", simVars.Count, context.Connection.RemoteIpAddress);
            }
            catch
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid request body.", context.RequestAborted);
                wsLogger.LogInformation("WS closed (invalid body) from {RemoteIp}", context.Connection.RemoteIpAddress);
                return;
            }

            // Get SimVarService from DI
            var simVarService = context.RequestServices.GetRequiredService<SimVarService>();

            // Track last sent values to support "only-on-change" mode (default).
            var lastValues = new Dictionary<string, double>();
            static bool ValuesEqual(double a, double b)
            {
                // Treat NaN==NaN as equal; otherwise use a small tolerance to avoid spamming on tiny noise.
                if (double.IsNaN(a) && double.IsNaN(b)) return true;
                if (double.IsNaN(a) || double.IsNaN(b)) return false;
                return System.Math.Abs(a - b) <=1e-6;
            }
            static string KeyOf(SimVarReference v) => $"{v.SimVarName}|{v.Unit}";

            while (true)
            {
                // Proactively detect client-initiated close frames. See DetectDisconnect() for rationale.
                if (await DetectDisconnect(webSocket, context.RequestAborted))
                    break;

                List<SimVarReference> resultsList;
                try
                {
                    resultsList = await simVarService.GetMultipleSimVarValuesAsync(simVars);
                }
                catch
                {
                    resultsList = simVars.Select(v => v with { Value = double.NaN }).ToList();
                }

                bool anyChanged = false;
                if (!alwaysUpdate)
                {
                    foreach (var r in resultsList)
                    {
                        var key = KeyOf(r);
                        if (!lastValues.TryGetValue(key, out var prev) || !ValuesEqual(prev, r.Value))
                        {
                            anyChanged = true;
                        }
                        // Update cache
                        lastValues[key] = r.Value;
                    }
                }

                if (alwaysUpdate || anyChanged || lastValues.Count ==0)
                {
                    // Send full snapshot when anything changed (or in alwaysUpdate mode).
                    var json = JsonSerializer.Serialize(resultsList);
                    var sendBuffer = Encoding.UTF8.GetBytes(json);
                    try
                    {
                        await webSocket.SendAsync(sendBuffer, WebSocketMessageType.Text, true, context.RequestAborted);
                        if (webSocket.State != WebSocketState.Open || context.RequestAborted.IsCancellationRequested)
                            break;
                    }
                    catch
                    {
                        break;
                    }
                }

                try
                {
                    await Task.Delay(intervalSec *1000, context.RequestAborted);
                    if (webSocket.State != WebSocketState.Open || context.RequestAborted.IsCancellationRequested)
                        break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            wsLogger.LogInformation("WS /api/simvar/register closed for {RemoteIp}", context.Connection.RemoteIpAddress);
        });

        app.UseAuthorization();
        app.MapControllers();
        // Start the web server (non-blocking)
        var runTask = Task.Run(() => app.Run());
        return (port, localIp, app);
    }

    // Original overload now calls the new one and discards app
    public static (string port, string localIp) Start(WebApplicationBuilder builder)
    {
        var (port, localIp, _) = StartAndReturnApp(builder);
        return (port, localIp);
    }

    // Backward-compatible overload
    public static (string port, string localIp) Start(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddCommandLine(args);
        return Start(builder);
    }

    private static async Task<bool> DetectDisconnect(WebSocket webSocket, CancellationToken cancellationToken)
    {
        // Why this exists:
        // The server learns about client disconnects (Close frames) only when it performs a read.
        // If the server only writes, the close can go unnoticed and the loop keeps running.
        // We do a tiny timed ReceiveAsync to observe a close frame without blocking the loop.
        var probeBuffer = new byte[1];
        try
        {
            var receiveTask = webSocket.ReceiveAsync(probeBuffer, cancellationToken);
            var completed = await Task.WhenAny(receiveTask, Task.Delay(10, cancellationToken));
            if (completed == receiveTask)
            {
                var res = await receiveTask;
                if (res.MessageType == WebSocketMessageType.Close)
                    return true;
            }
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (WebSocketException)
        {
            return true;
        }
        catch
        {
            // ignore other errors and fall back to state check
        }
        return webSocket.State != WebSocketState.Open;
    }
}
