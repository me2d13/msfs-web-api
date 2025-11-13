using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Net;

public static class TrayIconManager
{
    public static void Start(string port, string localIp)
    {
        var trayThread = new Thread(() =>
        {
            NotifyIcon trayIcon = new NotifyIcon();
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream iconStream = assembly.GetManifestResourceStream("MsfsApiServer.Resources.plane32.ico"))
                {
                    if (iconStream != null)
                        trayIcon.Icon = new Icon(iconStream);
                    else
                        trayIcon.Icon = SystemIcons.Application;
                }
            }
            catch
            {
                trayIcon.Icon = SystemIcons.Application;
            }
            trayIcon.Text = $"MSFS Web API listening at:\nhttp://localhost:{port}\nhttp://{localIp}:{port}";
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
            trayIcon.ContextMenuStrip = contextMenu;
            trayIcon.Visible = true;
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
            trayIcon.Visible = false;
            trayIcon.Dispose();
            Environment.Exit(0);
        };
            trayIcon.MouseUp += (s, e) =>
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
}
