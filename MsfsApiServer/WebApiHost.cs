using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SimConnector;
using System.Net;

public static class WebApiHost
{
    public static (string port, string localIp) Start(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddCommandLine(args);
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
        app.UseAuthorization();
        app.MapControllers();
        // Start the web server (non-blocking)
        var runTask = Task.Run(() => app.Run());
        return (port, localIp);
    }
}
