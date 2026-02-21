using YamlDotNet.Serialization;

namespace MsfsApiServer.Configuration
{
    /// <summary>
    /// Root configuration model matching config.yaml structure
    /// </summary>
    public class AppConfig
    {
        [YamlMember(Alias = "general")]
        public GeneralConfig General { get; set; } = new();

        [YamlMember(Alias = "webApi")]
        public WebApiConfig WebApi { get; set; } = new();

        [YamlMember(Alias = "udp")]
        public UdpConfig Udp { get; set; } = new();

        [YamlMember(Alias = "serial")]
        public SerialConfig Serial { get; set; } = new();
    }

    public class GeneralConfig
    {
        [YamlMember(Alias = "logFile")]
        public string? LogFile { get; set; }

        [YamlMember(Alias = "logLevel")]
        public string? LogLevel { get; set; }
    }

    public class WebApiConfig
    {
        [YamlMember(Alias = "port")]
        public int? Port { get; set; }
    }

    public class UdpConfig
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; } = false;

        [YamlMember(Alias = "targetHost")]
        public string? TargetHost { get; set; }

        [YamlMember(Alias = "targetPort")]
        public int? TargetPort { get; set; }

        [YamlMember(Alias = "interval")]
        public int? Interval { get; set; }

        [YamlMember(Alias = "variables")]
        public List<string> Variables { get; set; } = new();
    }

    public class SerialConfig
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; } = false;

        /// <summary>COM port name, e.g. "COM3" or "/dev/ttyUSB0"</summary>
        [YamlMember(Alias = "comPort")]
        public string? ComPort { get; set; }

        /// <summary>Baud rate, e.g. 9600, 115200</summary>
        [YamlMember(Alias = "baudRate")]
        public int BaudRate { get; set; } = 9600;

        /// <summary>Parity: None, Odd, Even, Mark, Space (default: None)</summary>
        [YamlMember(Alias = "parity")]
        public string Parity { get; set; } = "None";

        /// <summary>Data bits: 5, 6, 7, or 8 (default: 8)</summary>
        [YamlMember(Alias = "dataBits")]
        public int DataBits { get; set; } = 8;

        /// <summary>Stop bits: One, OnePointFive, Two (default: One)</summary>
        [YamlMember(Alias = "stopBits")]
        public string StopBits { get; set; } = "One";

        /// <summary>Handshake: None, XOnXOff, RequestToSend, RequestToSendXOnXOff (default: None)</summary>
        [YamlMember(Alias = "handshake")]
        public string Handshake { get; set; } = "None";

        [YamlMember(Alias = "interval")]
        public int? Interval { get; set; }

        [YamlMember(Alias = "variables")]
        public List<string> Variables { get; set; } = new();
    }

    /// <summary>
    /// Diagnostics information about configuration loading.
    /// </summary>
    public class ConfigDiagnostics
    {
        public string ConfigFilePath { get; set; } = string.Empty;
        public bool ConfigFileFound { get; set; }
        public bool ConfigFileLoaded { get; set; }
        public string? LoadError { get; set; }
        public List<string> CommandLineOverrides { get; set; } = new();

        public string GetSummary()
        {
            var parts = new List<string>();

            parts.Add($"Config file: '{ConfigFilePath}'");

            if (!ConfigFileFound)
            {
                parts.Add("(not found, using defaults)");
            }
            else if (!ConfigFileLoaded)
            {
                parts.Add($"(found but failed to load: {LoadError})");
            }
            else
            {
                parts.Add("(loaded successfully)");
            }

            if (CommandLineOverrides.Count > 0)
            {
                parts.Add($"Command-line overrides: {string.Join(", ", CommandLineOverrides)}");
            }

            return string.Join(" ", parts);
        }
    }

    /// <summary>
    /// Result of configuration loading containing both config and diagnostics.
    /// </summary>
    public class ConfigLoadResult
    {
        public AppConfig Config { get; init; } = new();
        public ConfigDiagnostics Diagnostics { get; init; } = new();
    }
}
