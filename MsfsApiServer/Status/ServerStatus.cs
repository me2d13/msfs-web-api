using System.Reflection;
using System.Runtime.InteropServices;
using MsfsApiServer.Configuration;
using MsfsApiServer.Serial;
using MsfsApiServer.Udp;
using SimConnector;

namespace MsfsApiServer.Status
{
    /// <summary>
    /// Runtime facts about this instance of the server: filled in by Program at startup and
    /// read by the status page and /api/stats.
    /// </summary>
    public class ServerStatus
    {
        public ServerStatus()
        {
            ExePath = ResolveExePath();
            Version = ResolveVersion();
        }

        public AppConfig Config { get; private set; } = new();
        public ConfigDiagnostics ConfigDiagnostics { get; private set; } = new();
        public string? LogFilePath { get; private set; }

        /// <summary>Port the API actually listens on.</summary>
        public string Port { get; set; } = string.Empty;

        /// <summary>IPv4 address the API is reachable at from the network.</summary>
        public string LocalIp { get; set; } = string.Empty;

        /// <summary>Set by Program once the stream is running; null when it is disabled or failed to start.</summary>
        public UdpStreamingService? Udp { get; set; }

        /// <summary>Set by Program once the stream is running; null when it is disabled or failed to start.</summary>
        public SerialStreamingService? Serial { get; set; }

        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public string ExePath { get; }
        public string Version { get; }

        public string ExeName => Path.GetFileName(ExePath);
        public string BaseDir => Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;

        /// <summary>Directory next to the exe whose pages the status page lists and serves.</summary>
        public string WwwDir => Path.Combine(BaseDir, "www");

        /// <summary>Records what the process was started with.</summary>
        public void Configure(AppConfig config, ConfigDiagnostics diagnostics, string? logFilePath)
        {
            Config = config;
            ConfigDiagnostics = diagnostics;
            LogFilePath = logFilePath;
        }

        /// <summary>Creates the www directory if needed, so the note on the status page is not a lie.</summary>
        public void EnsureWwwDirectory()
        {
            try
            {
                Directory.CreateDirectory(WwwDir);
            }
            catch
            {
                // A read-only install simply has no local pages.
            }
        }

        /// <summary>Collects the current state for the status page.</summary>
        public StatusSnapshot BuildSnapshot(SimVarService simVarService, SimConnectClient simClient)
        {
            var statistics = simVarService.GetStatistics();
            var variables = statistics.Snapshot();
            var logExists = !string.IsNullOrEmpty(LogFilePath) && File.Exists(LogFilePath);
            var areas = simVarService.GetClientDataService().GetKnownAreas();

            return new StatusSnapshot
            {
                Server = new ServerInfo
                {
                    ExeName = ExeName,
                    ExePath = ExePath,
                    BaseDir = BaseDir,
                    Version = Version,
                    ProcessId = Environment.ProcessId,
                    StartedUtc = StartedUtc,
                    UptimeSeconds = (long)(DateTime.UtcNow - StartedUtc).TotalSeconds,
                    Port = Port,
                    LocalIp = LocalIp,
                    Url = $"http://localhost:{Port}/",
                    NetworkUrl = $"http://{LocalIp}:{Port}/",
                    MachineName = Environment.MachineName,
                    Framework = RuntimeInformation.FrameworkDescription,
                    LogFilePath = string.IsNullOrEmpty(LogFilePath) ? null : LogFilePath,
                    LogFileExists = logExists,
                    LogFileSizeBytes = logExists ? TryGetFileSize(LogFilePath!) : null,
                },
                Sim = new SimInfo
                {
                    Connected = simClient.IsConnected,
                    KnownClientDataAreas = areas.Count,
                    ClientDataAreas = areas
                        .OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(a => new ClientAreaInfo { Name = a.Key, DataAreaId = a.Value })
                        .ToList(),
                },
                Config = new ConfigInfo
                {
                    FilePath = string.IsNullOrEmpty(ConfigDiagnostics.ConfigFilePath) ? null : ConfigDiagnostics.ConfigFilePath,
                    FileFound = ConfigDiagnostics.ConfigFileFound,
                    FileLoaded = ConfigDiagnostics.ConfigFileLoaded,
                    LoadError = ConfigDiagnostics.LoadError,
                    CommandLineOverrides = ConfigDiagnostics.CommandLineOverrides,
                    LogLevel = Config.General.LogLevel,
                    UdpEnabled = Config.Udp.Enabled,
                    SerialEnabled = Config.Serial.Enabled,
                },
                Udp = Udp?.GetStatus(),
                Serial = Serial?.GetStatus(),
                Totals = new TotalsInfo
                {
                    StartedUtc = statistics.StartedUtc,
                    Reads = statistics.TotalReads,
                    Writes = statistics.TotalWrites,
                    NoValue = statistics.TotalNoValue,
                    Variables = variables.Count,
                },
                Variables = variables,
                Www = new WwwInfo
                {
                    Dir = WwwDir,
                    DirExists = Directory.Exists(WwwDir),
                    Files = WwwFiles.List(WwwDir),
                },
            };
        }

        private static long? TryGetFileSize(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveExePath()
        {
            // ProcessPath is the apphost (MsfsApiServer.exe); the assembly lives beside it.
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath)) return processPath;

            try
            {
                return Assembly.GetEntryAssembly()?.Location ?? AppContext.BaseDirectory;
            }
            catch
            {
                return AppContext.BaseDirectory;
            }
        }

        private static string ResolveVersion()
        {
            try
            {
                var assembly = Assembly.GetEntryAssembly();
                return assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? assembly?.GetName().Version?.ToString()
                    ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
