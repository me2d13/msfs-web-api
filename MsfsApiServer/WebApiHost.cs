using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MsfsApiServer.Status;
using SimConnector;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            // Reports the state the connection monitor maintains. It deliberately does not call
            // RefreshConnection(): that drops the SimConnect connection, which would clear the
            // client data subscriptions on every poll. The monitor reconnects within 5 seconds.
            string connectionStatus = simClient.IsConnected ? "CONNECTED" : "DISCONNECTED";
            return $"API Server Running. MSFS Connection Status: {connectionStatus}";
        });

        // Everything the status page shows, as JSON.
        // Note: no RefreshConnection() here. That helper drops and recreates the SimConnect
        // connection, which would clear the client data subscriptions on every poll of a page
        // that refreshes every couple of seconds; the connection monitor keeps IsConnected honest.
        app.MapGet("/api/stats", (ServerStatus status, SimVarService simVarService, SimConnectClient simClient) =>
        {
            var snapshot = status.BuildSnapshot(simVarService, simClient);
            return Results.Text(JsonSerializer.Serialize(snapshot, StatsJsonOptions), "application/json");
        });
        
        app.MapPost("/api/simvar/get", async (SimVarService simVarService, SimVarReference reference) =>
        {
            // Normalize reference to parse alias if embedded in SimVarName
            var normalized = SimVarParser.NormalizeReference(reference);
            var result = await simVarService.GetSimVarValueAsync(normalized);
            if (result == null) return Results.Text("null", "application/json");
            
            // Return as {outputName: value}
            var response = new Dictionary<string, double?> { { result.GetOutputName(), JsonNumber(result.Value) } };
            return Results.Json(response);
        });
        
        app.MapPost("/api/simvar/set", async (SimVarService simVarService, SimVarReference reference) =>
        {
            // Normalize reference to parse alias if embedded in SimVarName
            var normalized = SimVarParser.NormalizeReference(reference);
            var result = await simVarService.SetSimVarValueAsync(normalized);
            if (result == null) return Results.Text("null", "application/json");
            
            // Return as {outputName: value}
            var response = new Dictionary<string, double> { { result.GetOutputName(), result.Value } };
            return Results.Json(response);
        });
        
        app.MapPost("/api/simvar/getMultiple", async (SimVarService simVarService, List<SimVarReference> references) =>
        {
            // Normalize all references
            var normalized = references.Select(SimVarParser.NormalizeReference).ToList();
            var results = await simVarService.GetMultipleSimVarValuesAsync(normalized);
            
            // Return as {outputName: value, ...}
            var response = results.ToDictionary(r => r.GetOutputName(), r => JsonNumber(r.Value));
            return Results.Json(response);
        });
        
        app.MapPost("/api/simvar/setMultiple", async (SimVarService simVarService, List<SimVarReference> references) =>
        {
            // Normalize all references
            var normalized = references.Select(SimVarParser.NormalizeReference).ToList();
            var results = await simVarService.SetMultipleSimVarValuesAsync(normalized);
            
            // Return as {outputName: value, ...}
            var response = results.ToDictionary(r => r.GetOutputName(), r => r.Value);
            return Results.Json(response);
        });
        
        app.MapPost("/api/event/send", (SimEventService simEventService, EventReference reference) =>
        {
            simEventService.SendEvent(reference);
            return Results.Ok("OK");
        });

        // One-shot read of a byte range of a client data area, as base64: the PMDG 737 CDU screen
        // is 1009 bytes at offset 0 of PMDG_NG3_CDU_0 / PMDG_NG3_CDU_1. An area this server knows
        // can be named without its ID; anything else has to publish one.
        //   GET /api/clientdata/raw?area=PMDG_NG3_CDU_0&offset=0&length=1009
        //   GET /api/clientdata/raw?area=MY_AREA&areaId=0x4E473331&offset=16&length=64
        app.MapGet("/api/clientdata/raw", async (SimVarService simVarService, HttpContext context) =>
        {
            var query = context.Request.Query;
            int? areaId = TryParseNumber((string?)query["areaId"], out int parsedAreaId) ? parsedAreaId : null;
            int? offset = TryParseNumber((string?)query["offset"], out int parsedOffset) ? parsedOffset : null;
            int? length = TryParseNumber((string?)query["length"], out int parsedLength) ? parsedLength : null;

            if (!TryValidateRange((string?)query["area"], areaId, offset, length,
                    out string area, out int resolvedAreaId, out int resolvedOffset, out int resolvedLength, out string error))
            {
                return Results.BadRequest(error);
            }

            var bytes = await simVarService.GetClientDataService()
                .GetRawBytesAsync(area, resolvedAreaId, resolvedOffset, resolvedLength);

            return Results.Text(ClientDataRangeJson(area, resolvedOffset, resolvedLength, bytes), "application/json");
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
                var rawRefs = JsonSerializer.Deserialize<List<SimVarReference>>(jsonString, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (rawRefs == null || rawRefs.Count ==0)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "No sim variables provided.", context.RequestAborted);
                    wsLogger.LogInformation("WS closed (no variables) from {RemoteIp}", context.Connection.RemoteIpAddress);
                    return;
                }
                // Normalize references to parse aliases
                simVars = rawRefs.Select(SimVarParser.NormalizeReference).ToList();
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
            // Use output name as key
            static string KeyOf(SimVarReference v) => v.GetOutputName();

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
                    // Identify invalid numeric values prior to serialization
                    var invalidKeys = resultsList
                        .Where(r => double.IsNaN(r.Value) || double.IsInfinity(r.Value))
                        .Select(r => r.GetOutputName())
                        .ToList();

                    if (invalidKeys.Count == resultsList.Count)
                    {
                        // All invalid -> skip send
                        wsLogger.LogDebug("All SimVar values invalid (NaN/Infinity); skipping WS send");
                    }
                    else
                    {
                        if (invalidKeys.Count >0)
                        {
                            wsLogger.LogWarning("Excluding {Count} invalid SimVar values from WS send: {Keys}", invalidKeys.Count, string.Join(",", invalidKeys));
                        }

                        // Build response only from valid values
                        var response = resultsList
                            .Where(r => !(double.IsNaN(r.Value) || double.IsInfinity(r.Value)))
                            .ToDictionary(r => r.GetOutputName(), r => r.Value);

                        try
                        {
                            var json = JsonSerializer.Serialize(response);
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
                        catch (Exception ex)
                        {
                            // Log offending values (those we attempted to serialize)
                            wsLogger.LogError(ex, "Failed to serialize WS SimVar payload. Values: {Values}", string.Join(";", response.Select(kv => kv.Key + "=" + kv.Value)));
                            // Skip sending this cycle
                        }
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

        // WebSocket endpoint for a byte range of a client data area, for data that is not a number
        // per field: the PMDG 737 CDU screen (24x14 cells of symbol/color/flags plus a Powered bool)
        // is one such block. The first text message describes the range:
        //   {"area":"PMDG_NG3_CDU_0","offset":0,"length":1009,"intervalMs":200}
        // areaId is only needed for areas this server does not know; intervalMs defaults to 200 and
        // alwaysUpdate (query or body) sends the range every interval instead of on change only.
        app.Map("/api/clientdata/register", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("WebSocket connection expected.");
                return;
            }

            var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
            var wsLogger = loggerFactory.CreateLogger("ClientDataWebSocket");

            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();

            // First message: which range to stream.
            var buffer = new byte[4096];
            WebSocketReceiveResult? result = null;
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
            if (result == null || result.MessageType != WebSocketMessageType.Text)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Expected text message", context.RequestAborted);
                wsLogger.LogInformation("WS closed (no/invalid first message) from {RemoteIp}", context.Connection.RemoteIpAddress);
                return;
            }

            ClientDataRangeRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<ClientDataRangeRequest>(
                    Encoding.UTF8.GetString(buffer, 0, result.Count),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid request body.", context.RequestAborted);
                wsLogger.LogInformation("WS closed (invalid body) from {RemoteIp}", context.Connection.RemoteIpAddress);
                return;
            }

            // Query parameters may override the body, which makes the endpoint usable from a
            // generic WebSocket client without building a request message.
            int? intervalOverride = TryParseNumber((string?)context.Request.Query["intervalMs"], out int parsedInterval) ? parsedInterval : null;
            bool alwaysUpdate = request?.AlwaysUpdate
                ?? (TryParseFlag((string?)context.Request.Query["alwaysUpdate"], out bool parsedAlways) && parsedAlways);

            // A missing body is not special-cased: an empty range fails validation like any other.
            if (!TryValidateRange(request?.Area, request?.AreaId, request?.Offset, request?.Length,
                    out string area, out int areaId, out int offset, out int length, out string error))
            {
                await SendJsonAsync(webSocket, ClientDataRangeJson(null, 0, 0, null, error), context.RequestAborted);
                await webSocket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid range request.", context.RequestAborted);
                wsLogger.LogInformation("WS closed (invalid range: {Error}) from {RemoteIp}", error, context.Connection.RemoteIpAddress);
                return;
            }

            // A request without a body never gets here: the range validation above rejects it.
            int intervalMs = Math.Clamp(intervalOverride ?? request?.IntervalMs ?? 200, 50, 5000);
            var clientData = context.RequestServices.GetRequiredService<SimVarService>().GetClientDataService();

            wsLogger.LogInformation(
                "WS /api/clientdata/register opened from {RemoteIp}: {Area} offset={Offset} length={Length} interval={Interval}ms alwaysUpdate={Always}",
                context.Connection.RemoteIpAddress, area, offset, length, intervalMs, alwaysUpdate);

            string? lastPayload = null;
            while (true)
            {
                if (await DetectDisconnect(webSocket, context.RequestAborted)) break;

                byte[]? bytes = null;
                try
                {
                    bytes = await clientData.GetRawBytesAsync(area, areaId, offset, length);
                }
                catch (Exception ex)
                {
                    wsLogger.LogError(ex, "Client data range read failed for {Area}", area);
                }

                string payload = ClientDataRangeJson(area, offset, length, bytes);
                if (alwaysUpdate || payload != lastPayload)
                {
                    lastPayload = payload;
                    if (!await SendJsonAsync(webSocket, payload, context.RequestAborted)) break;
                }

                try
                {
                    await Task.Delay(intervalMs, context.RequestAborted);
                    if (webSocket.State != WebSocketState.Open || context.RequestAborted.IsCancellationRequested) break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            wsLogger.LogInformation("WS /api/clientdata/register closed for {RemoteIp}", context.Connection.RemoteIpAddress);
        });

        // Built-in status page, also reachable as /status.
        app.MapGet("/", (ServerStatus status) => Results.Content(StatusPage.Html(status.WwwDir), "text/html"));
        app.MapGet("/status", (ServerStatus status) => Results.Content(StatusPage.Html(status.WwwDir), "text/html"));

        // Pages and assets from the "www" directory next to the exe: /views.html, /img/plane.jpg.
        // A catch-all, so every literal route above keeps precedence over it.
        app.MapGet("/{**path}", (string? path, ServerStatus status, HttpContext context) =>
        {
            var file = WwwFiles.Resolve(status.WwwDir, path);
            if (file == null) return Results.NotFound($"Not found: /{path}");
            context.Response.Headers.CacheControl = "no-cache";
            return Results.File(file, WwwFiles.ContentTypeFor(file));
        });

        // The tray icon doubles as the page icon.
        app.MapGet("/favicon.ico", () =>
        {
            var icon = IconBytes();
            return icon.Length == 0 ? Results.NotFound() : Results.Bytes(icon, "image/x-icon");
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

    /// <summary>
    /// JSON has no NaN or Infinity literal and System.Text.Json refuses to write them, so an
    /// unreadable value (missing area, add-on not broadcasting, offset out of bounds) is reported
    /// as null - the same way the UDP and WebSocket paths drop it. Without this the response
    /// fails to serialize and the request ends as an empty HTTP 500.
    /// </summary>
    private static double? JsonNumber(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) ? null : value;
    }

    private static readonly JsonSerializerOptions StatsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Parses a decimal or 0x-prefixed hex number from a request parameter ("0x4E473335" is how
    /// the add-on SDKs publish client data area IDs).
    /// </summary>
    private static bool TryParseNumber(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Parses a boolean request parameter, accepting what a URL can reasonably carry: true/false,
    /// yes/no, on/off and 1/0.
    /// </summary>
    private static bool TryParseFlag(string? text, out bool value)
    {
        value = false;
        if (string.IsNullOrWhiteSpace(text)) return false;

        switch (text.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                value = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
                value = false;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Validates an area / areaId / offset / length range request. The area ID may be left out for
    /// areas this server knows, and the length is capped so one request cannot fan out into
    /// thousands of SimConnect definitions.
    /// </summary>
    private static bool TryValidateRange(
        string? area, int? areaId, int? offset, int? length,
        out string resolvedArea, out int resolvedAreaId, out int resolvedOffset, out int resolvedLength,
        out string error)
    {
        resolvedArea = area?.Trim() ?? string.Empty;
        resolvedAreaId = areaId ?? 0;
        resolvedOffset = offset ?? 0;
        resolvedLength = length ?? 0;
        error = string.Empty;

        if (resolvedArea.Length == 0)
        {
            error = "area is required";
            return false;
        }
        if (resolvedAreaId == 0 && !KnownClientDataAreas.TryGetId(resolvedArea, out resolvedAreaId))
        {
            error = $"unknown client data area '{resolvedArea}' - pass its ID, e.g. areaId=0x4E473331";
            return false;
        }
        if (resolvedOffset < 0)
        {
            error = "offset must be 0 or more";
            return false;
        }
        if (resolvedLength <= 0 || resolvedLength > ClientDataService.MaxRawRangeLength)
        {
            error = $"length must be 1..{ClientDataService.MaxRawRangeLength}";
            return false;
        }
        return true;
    }

    /// <summary>
    /// The payload both range endpoints send: the range that was read, whether the area answered
    /// (an add-on that is not loaded, or a broadcast that is off, is not reported as zeros), and
    /// the bytes as base64 when it did.
    /// </summary>
    private static string ClientDataRangeJson(string? area, int offset, int length, byte[]? bytes, string? error = null)
    {
        var response = new Dictionary<string, object?>
        {
            ["area"] = area,
            ["offset"] = offset,
            ["length"] = length,
            ["available"] = bytes != null,
            ["data"] = bytes == null ? null : Convert.ToBase64String(bytes)
        };
        if (error != null) response["error"] = error;
        return JsonSerializer.Serialize(response);
    }

    /// <summary>Sends one text frame; false when the socket can no longer be used.</summary>
    private static async Task<bool> SendJsonAsync(WebSocket webSocket, string json, CancellationToken cancellationToken)
    {
        try
        {
            await webSocket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken);
            return webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested;
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? _iconBytes;

    /// <summary>The tray icon, served as the page icon; empty when the resource is missing.</summary>
    private static byte[] IconBytes()
    {
        if (_iconBytes != null) return _iconBytes;

        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("MsfsApiServer.Resources.plane32.ico");
            if (stream != null)
            {
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                _iconBytes = memory.ToArray();
            }
        }
        catch
        {
            // No icon is a cosmetic problem only.
        }

        return _iconBytes ??= Array.Empty<byte>();
    }
}

/// <summary>
/// The first message of a <c>/api/clientdata/register</c> stream: which byte range of which client
/// data area to send, and how often. Every property except <see cref="Area"/> and
/// <see cref="Length"/> is optional (property names are matched case-insensitively).
/// </summary>
internal sealed class ClientDataRangeRequest
{
    public string? Area { get; set; }
    public int? AreaId { get; set; }
    public int? Offset { get; set; }
    public int? Length { get; set; }
    public int? IntervalMs { get; set; }
    public bool? AlwaysUpdate { get; set; }
}
