using System;
using System.IO;
using System.Reflection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MsfsApiServer.Configuration
{
    /// <summary>
    /// Loads and merges configuration from YAML file and command-line arguments.
    /// Command-line arguments take precedence over config file values.
    /// </summary>
    public static class ConfigLoader
    {
        /// <summary>
        /// Load configuration with precedence: defaults < config file < command-line args
        /// Returns both the config and diagnostics about how it was loaded.
        /// </summary>
        public static ConfigLoadResult Load(string[] args)
        {
            var config = new AppConfig();
            var diagnostics = new ConfigDiagnostics();

            // Parse command-line args first to get --config parameter
            var cmdLineConfig = ParseCommandLine(args);

            // Determine config file path
            string configFilePath = ResolveConfigFilePath(cmdLineConfig.ConfigFile);
            diagnostics.ConfigFilePath = configFilePath;

            // Load from YAML file if it exists
            if (!string.IsNullOrEmpty(configFilePath) && File.Exists(configFilePath))
            {
                diagnostics.ConfigFileFound = true;
                try
                {
                    config = LoadFromYaml(configFilePath);
                    diagnostics.ConfigFileLoaded = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Failed to load config file '{configFilePath}': {ex.Message}");
                    // Continue with defaults if config file fails
                    config = new AppConfig();
                    diagnostics.ConfigFileLoaded = false;
                    diagnostics.LoadError = ex.Message;
                }
            }
            else
            {
                diagnostics.ConfigFileFound = false;
                diagnostics.ConfigFileLoaded = false;
            }

            // Override with command-line arguments (highest priority)
            ApplyCommandLineOverrides(config, cmdLineConfig, diagnostics);

            return new ConfigLoadResult
            {
                Config = config,
                Diagnostics = diagnostics
            };
        }

        private static AppConfig LoadFromYaml(string filePath)
        {
            var yaml = File.ReadAllText(filePath);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            return deserializer.Deserialize<AppConfig>(yaml) ?? new AppConfig();
        }

        private static string ResolveConfigFilePath(string? configFileArg)
        {
            // If --config was specified, use it as-is
            if (!string.IsNullOrWhiteSpace(configFileArg))
                return configFileArg!;

            // Default: config.yaml next to the executable
            try
            {
                var exe = Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(exe))
                {
                    var dir = Path.GetDirectoryName(exe)!;
                    return Path.Combine(dir, "config.yaml");
                }
            }
            catch { }

            return "config.yaml"; // Fallback to current directory
        }

        private static CommandLineConfig ParseCommandLine(string[] args)
        {
            var result = new CommandLineConfig();

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--config" && i + 1 < args.Length)
                {
                    result.ConfigFile = args[++i];
                }
                else if (args[i] == "--port" && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out var port))
                        result.Port = port;
                }
                else if (args[i] == "--logFile" && i + 1 < args.Length)
                {
                    result.LogFile = args[++i];
                }
            }

            return result;
        }

        private static void ApplyCommandLineOverrides(AppConfig config, CommandLineConfig cmdLine, ConfigDiagnostics diagnostics)
        {
            // Command-line args override config file values
            if (cmdLine.Port.HasValue)
            {
                config.WebApi.Port = cmdLine.Port.Value;
                diagnostics.CommandLineOverrides.Add($"--port {cmdLine.Port.Value}");
            }

            if (!string.IsNullOrWhiteSpace(cmdLine.LogFile))
            {
                config.General.LogFile = cmdLine.LogFile;
                diagnostics.CommandLineOverrides.Add($"--logFile {cmdLine.LogFile}");
            }
        }

        private class CommandLineConfig
        {
            public string? ConfigFile { get; set; }
            public int? Port { get; set; }
            public string? LogFile { get; set; }
        }
    }
}
