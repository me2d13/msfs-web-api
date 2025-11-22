using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Net;
using System.Windows.Forms; // Added for NotifyIcon, MessageBox, etc.

public static class TrayIconManager
{
    private static NotifyIcon? _trayIcon;
    private static string _baseTooltipText = "";

    public static void Start(string port, string localIp)
    {
        _baseTooltipText = $"MSFS Web API listening at:\nhttp://localhost:{port}\nhttp://{localIp}:{port}";
        
        var trayThread = new Thread(() =>
        {
            _trayIcon = new NotifyIcon();
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream iconStream = assembly.GetManifestResourceStream("MsfsApiServer.Resources.plane32.ico"))
                {
                    if (iconStream != null)
                        _trayIcon.Icon = new Icon(iconStream);
                    else
                        _trayIcon.Icon = SystemIcons.Application;
                }
            }
            catch
            {
                _trayIcon.Icon = SystemIcons.Application;
            }
            
            UpdateTooltip(false); // Default to not connected

            var contextMenu = new ContextMenuStrip();
            var showApiDocItem = new ToolStripMenuItem("Show API doc");
            var copyUrlItem = new ToolStripMenuItem("Copy network URL");
            var openGithubItem = new ToolStripMenuItem("Open Github page");
            var quitItem = new ToolStripMenuItem("Quit");
            contextMenu.Items.Add(showApiDocItem);
            contextMenu.Items.Add(copyUrlItem);
            contextMenu.Items.Add(openGithubItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(quitItem);
            _trayIcon.ContextMenuStrip = contextMenu;
            _trayIcon.Visible = true;
            showApiDocItem.Click += (s, e) =>
            {
                var swaggerUrl = $"http://localhost:{port}/swagger";
                Process.Start(new ProcessStartInfo(swaggerUrl) { UseShellExecute = true });
            };
            copyUrlItem.Click += (s, e) =>
            {
                var networkUrl = $"http://{localIp}:{port}";
                try { Clipboard.SetText(networkUrl); }
                catch (Exception ex) { MessageBox.Show($"Error copying to clipboard: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            openGithubItem.Click += (s, e) =>
            {
                var githubUrl = "https://github.com/me2d13/msfs-web-api";
                try
                {
                    Process.Start(new ProcessStartInfo(githubUrl) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error opening Github page: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            quitItem.Click += (s, e) =>
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                Environment.Exit(0);
            };
            _trayIcon.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                    contextMenu.Show(Cursor.Position);
            };
            Application.Run();
        });
        trayThread.IsBackground = true;
        trayThread.SetApartmentState(ApartmentState.STA);
        trayThread.Start();
    }

    public static void SetSimConnectStatus(bool isConnected)
    {
        UpdateTooltip(isConnected);
    }

    private static void UpdateTooltip(bool isConnected)
    {
        if (_trayIcon == null) return;
        var status = isConnected ? "Connected to MSFS" : "Not connected to MSFS";
        var text = $"{_baseTooltipText}\n{status}";
        // NotifyIcon.Text is limited to 63 chars on old Windows, but 127 on newer. 
        // We should be careful, but usually it's fine on Win10+.
        // If text is too long, it might throw or truncate.
        if (text.Length >= 128)
        {
            text = text.Substring(0, 127);
        }
        
        // Invoke not needed for NotifyIcon properties usually, but let's be safe if we could.
        // Since we don't have a Control to Invoke on, we rely on NotifyIcon's internal marshaling or thread safety.
        try
        {
            _trayIcon.Text = text;
        }
        catch { }
    }
}
