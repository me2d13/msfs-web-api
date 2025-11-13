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
    }

    public class GeneralConfig
    {
        [YamlMember(Alias = "logFile")]
        public string? LogFile { get; set; }
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
