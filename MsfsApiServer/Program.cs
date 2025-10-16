using SimConnector;
using System.Diagnostics;
using System.Reflection;
using System.IO;

const string ResourceName = "MsfsApiServer.Resources.plane32.ico";

var builder = WebApplication.CreateBuilder(args);

// Add command line configuration source for optional port parameter
builder.Configuration.AddCommandLine(args);

// Get port from command line args or use default
var port = builder.Configuration["port"] ?? "5018";
var url = $"http://localhost:{port}";
builder.WebHost.UseUrls(url);

builder.Services.AddSingleton<SimConnector.SimConnectClient>();

// Register core services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Enable Swagger UI for API documentation
app.UseSwagger();
app.UseSwaggerUI();

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

    trayIcon.Text = $"MSFS Web API listening at {url}";

    var contextMenu = new ContextMenuStrip();
    var showApiDocItem = new ToolStripMenuItem("Show API doc");
    var quitItem = new ToolStripMenuItem("Quit");
    contextMenu.Items.Add(showApiDocItem);
    contextMenu.Items.Add(quitItem);
    trayIcon.ContextMenuStrip = contextMenu;
    trayIcon.Visible = true;

    // Open Swagger UI in browser when menu item is clicked
    showApiDocItem.Click += (s, e) =>
    {
        var swaggerUrl = $"http://localhost:{port}/swagger";
        Process.Start(new ProcessStartInfo(swaggerUrl) { UseShellExecute = true });
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

app.UseAuthorization();
app.MapControllers();

// Start the web server
app.Run();
