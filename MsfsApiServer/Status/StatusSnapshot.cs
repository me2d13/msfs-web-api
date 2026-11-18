using SimConnector;

namespace MsfsApiServer.Status
{
    /// <summary>
    /// Everything /api/stats reports; the status page renders exactly this JSON.
    /// </summary>
    public record StatusSnapshot
    {
        public ServerInfo Server { get; init; } = new();
        public SimInfo Sim { get; init; } = new();
        public ConfigInfo Config { get; init; } = new();
        public StreamInfo? Udp { get; init; }
        public StreamInfo? Serial { get; init; }
        public TotalsInfo Totals { get; init; } = new();
        public List<SimVarStatEntry> Variables { get; init; } = new();
        public WwwInfo Www { get; init; } = new();
    }

    /// <summary>The process itself: what runs, from where, since when, and where it writes.</summary>
    public record ServerInfo
    {
        public string ExeName { get; init; } = string.Empty;
        public string ExePath { get; init; } = string.Empty;
        public string BaseDir { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public int ProcessId { get; init; }
        public DateTime StartedUtc { get; init; }
        public long UptimeSeconds { get; init; }
        public string Port { get; init; } = string.Empty;
        public string LocalIp { get; init; } = string.Empty;
        public string Url { get; init; } = string.Empty;
        public string NetworkUrl { get; init; } = string.Empty;
        public string MachineName { get; init; } = string.Empty;
        public string Framework { get; init; } = string.Empty;
        public string? LogFilePath { get; init; }
        public bool LogFileExists { get; init; }
        public long? LogFileSizeBytes { get; init; }
    }

    /// <summary>SimConnect connection state and the client data areas seen this session.</summary>
    public record SimInfo
    {
        public bool Connected { get; init; }
        public int KnownClientDataAreas { get; init; }
        public List<ClientAreaInfo> ClientDataAreas { get; init; } = new();
    }

    public record ClientAreaInfo
    {
        public string Name { get; init; } = string.Empty;

        /// <summary>SimConnect data area ID, decimal.</summary>
        public int DataAreaId { get; init; }
    }

    /// <summary>How the running instance was configured.</summary>
    public record ConfigInfo
    {
        public string? FilePath { get; init; }
        public bool FileFound { get; init; }
        public bool FileLoaded { get; init; }
        public string? LoadError { get; init; }
        public List<string> CommandLineOverrides { get; init; } = new();
        public string? LogLevel { get; init; }
        public bool UdpEnabled { get; init; }
        public bool SerialEnabled { get; init; }
    }

    /// <summary>State of one output stream (UDP or serial).</summary>
    public record StreamInfo
    {
        public string Type { get; init; } = string.Empty;
        public string? Target { get; init; }
        public int IntervalMs { get; init; }
        public int VariableCount { get; init; }
        public List<string> Variables { get; init; } = new();
        public bool Connected { get; init; }
        public long Sent { get; init; }
        public DateTime? LastSentUtc { get; init; }
        public int ConsecutiveFailures { get; init; }
        public string? LastError { get; init; }
    }

    /// <summary>Aggregate lookup counters.</summary>
    public record TotalsInfo
    {
        public DateTime StartedUtc { get; init; }
        public long Reads { get; init; }
        public long Writes { get; init; }
        public long NoValue { get; init; }
        public int Variables { get; init; }
    }

    /// <summary>The "www" directory next to the exe and the pages found in it.</summary>
    public record WwwInfo
    {
        public string Dir { get; init; } = string.Empty;
        public bool DirExists { get; init; }
        public List<WwwFileInfo> Files { get; init; } = new();
    }

    public record WwwFileInfo
    {
        public string Name { get; init; } = string.Empty;

        /// <summary>Name without extension, used as the link text.</summary>
        public string Title { get; init; } = string.Empty;

        public string Url { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public DateTime ModifiedUtc { get; init; }
    }
}
