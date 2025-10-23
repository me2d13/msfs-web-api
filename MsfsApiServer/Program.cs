using SimConnector;
using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Net;

const string ResourceName = "MsfsApiServer.Resources.plane32.ico";

var builder = WebApplication.CreateBuilder(args);

// Add command line configuration source for optional port parameter
builder.Configuration.AddCommandLine(args);

// Get port from command line args or use default
var port = builder.Configuration["port"] ?? "5018";
// Use 0.0.0.0 to listen on all network interfaces
var url = $"http://0.0.0.0:{port}";
builder.WebHost.UseUrls(url);

builder.Services.AddSingleton<SimConnector.SimConnectClient>();

// Configure CORS to allow requests from any origin
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
  {
        policy
         .AllowAnyOrigin()
            .AllowAnyHeader()
       .AllowAnyMethod();
    });
});

// Register core services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Enable CORS
app.UseCors();

// Enable Swagger UI for API documentation
app.UseSwagger();
app.UseSwaggerUI();

// Get local IP address for display in tray icon
string localIp = "0.0.0.0";
try
{
    var host = Dns.GetHostEntry(Dns.GetHostName());
    // Get first IPv4 address
    localIp = host.AddressList
        .FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        ?.ToString() ?? "0.0.0.0";
}
catch (Exception ex)
{
    Console.WriteLine($"Error getting local IP: {ex.Message}");
}

// Start tray icon in a background STA thread
var trayThread = new Thread(() =>
{
    // Create and configure the tray icon and its context menu
    NotifyIcon trayIcon = new NotifyIcon();

    try
    {
        // Load embedded icon resource for tray icon
        Assembly assembly = Assembly.GetExecutingAssembly();
        using (Stream iconStream = assembly.GetManifestResourceStream(ResourceName))
        {
            if (iconStream != null)
            {
                trayIcon.Icon = new Icon(iconStream);
            }
            else
            {
                Console.WriteLine($"Error: Embedded resource '{ResourceName}' not found. Using default icon.");
                trayIcon.Icon = SystemIcons.Application;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error loading embedded icon: {ex.Message}. Using default icon.");
        trayIcon.Icon = SystemIcons.Application;
    }

    // Show both localhost and network IP in tray tooltip
    trayIcon.Text = $"MSFS Web API listening at:\nhttp://localhost:{port}\nhttp://{localIp}:{port}";

    var contextMenu = new ContextMenuStrip();
    var showApiDocItem = new ToolStripMenuItem("Show API doc");
    var copyUrlItem = new ToolStripMenuItem("Copy network URL");
    var quitItem = new ToolStripMenuItem("Quit");
    contextMenu.Items.Add(showApiDocItem);
    contextMenu.Items.Add(copyUrlItem);
    contextMenu.Items.Add(new ToolStripSeparator());
    contextMenu.Items.Add(quitItem);
    trayIcon.ContextMenuStrip = contextMenu;
    trayIcon.Visible = true;

    // Open Swagger UI in browser when menu item is clicked
    showApiDocItem.Click += (s, e) =>
    {
        var swaggerUrl = $"http://localhost:{port}/swagger";
        Process.Start(new ProcessStartInfo(swaggerUrl) { UseShellExecute = true });
    };

    // Copy network URL to clipboard
    copyUrlItem.Click += (s, e) =>
    {
        var networkUrl = $"http://{localIp}:{port}";
        try
        {
            Clipboard.SetText(networkUrl);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error copying to clipboard: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    };

    // Handle quit action: dispose tray icon and exit application
    quitItem.Click += (s, e) =>
    {
        trayIcon.Visible = false;
        trayIcon.Dispose();
        // For a graceful shutdown, use app.Lifetime.StopApplication() if available
        Environment.Exit(0);
        Application.Exit();
    };

    // Show context menu on right-click (handled by OS, but explicit call is harmless)
    trayIcon.MouseUp += (s, e) =>
    {
        if (e.Button == MouseButtons.Right)
        {
            contextMenu.Show(Cursor.Position);
        }
    };

    // Start the Windows Forms message loop for the tray icon
    Application.Run();
});

trayThread.IsBackground = true;
trayThread.SetApartmentState(ApartmentState.STA); // Required for Windows Forms
trayThread.Start();

// Map API endpoints
app.MapGet("/api/status", (SimConnectClient simClient) =>
{
    simClient.RefreshConnection();
    string connectionStatus = simClient.IsConnected ? "CONNECTED" : "DISCONNECTED";
    return $"API Server Running. MSFS Connection Status: {connectionStatus}";
});

app.MapPost("/api/simvar/get", async (SimConnectClient simClient, SimVarReference reference) =>
{
    var result = await simClient.GetSimVarValueAsync(reference);
    return Results.Json(result);
});

app.MapPost("/api/simvar/set", async (SimConnectClient simClient, SimVarReference reference) =>
{
    var result = await simClient.SetSimVarValueAsync(reference);
    return Results.Json(result);
});

app.MapPost("/api/simvar/getMultiple", async (SimConnectClient simClient, List<SimVarReference> references) =>
{
    var results = await simClient.GetMultipleSimVarValuesAsync(references);
    return Results.Json(results);
});

app.MapPost("/api/simvar/setMultiple", async (SimConnectClient simClient, List<SimVarReference> references) =>
{
    var results = await simClient.SetMultipleSimVarValuesAsync(references);
    return Results.Json(results);
});

app.MapPost("/api/event/send", async (SimConnectClient simClient, EventReference reference) =>
{
    simClient.SendEvent(reference);
    return "OK";
});

app.UseAuthorization();
app.MapControllers();

// Start the web server
app.Run();
