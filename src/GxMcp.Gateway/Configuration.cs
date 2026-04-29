using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using System.Threading;
using System.Collections.Generic;

namespace GxMcp.Gateway
{
    public class Configuration
    {
        [JsonProperty("GeneXus")]
        public GeneXusConfig? GeneXus { get; set; }

        [JsonProperty("Server")]
        public ServerConfig? Server { get; set; }

        [JsonProperty("Logging")]
        public LoggingConfig? Logging { get; set; }

        [JsonProperty("Environment")]
        public EnvironmentConfig? Environment { get; set; }

        public static string? CurrentConfigPath { get; private set; }
        private static FileSystemWatcher? _watcher;
        public static event Action<Configuration>? OnConfigurationChanged;

        public static Configuration Load()
        {
            if (CurrentConfigPath == null)
            {
                string? explicitConfigPath = global::System.Environment.GetEnvironmentVariable("GX_CONFIG_PATH");
                if (!string.IsNullOrWhiteSpace(explicitConfigPath))
                {
                    string fullPath = Path.GetFullPath(explicitConfigPath);
                    if (!File.Exists(fullPath))
                    {
                        throw new FileNotFoundException($"GX_CONFIG_PATH points to a missing config.json: {fullPath}");
                    }

                    CurrentConfigPath = fullPath;
                }
                else
                {
                    // Reliable path discovery: look for config.json starting from .exe up to root
                    string? currentDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

                    while (currentDir != null)
                    {
                        string check = Path.Combine(currentDir, "config.json");
                        if (File.Exists(check)) { CurrentConfigPath = check; break; }
                        currentDir = Path.GetDirectoryName(currentDir);
                    }

                    if (CurrentConfigPath == null)
                    {
                        if (File.Exists("config.json")) CurrentConfigPath = Path.GetFullPath("config.json");
                        else throw new FileNotFoundException("Could not find config.json in any parent directory.");
                    }
                }
            }

            Program.Log($"[Gateway] Loading config from: {CurrentConfigPath}");
            var config = ParseConfig(CurrentConfigPath);

            SetupWatcher(CurrentConfigPath);

            return config;
        }

        private static Configuration ParseConfig(string path)
        {
            // Retry logic for file locks
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    string json = File.ReadAllText(path);
                    var config = JsonConvert.DeserializeObject<Configuration>(json);
                    if (config == null) throw new Exception("Failed to parse config.json");
                    
                    if (string.IsNullOrEmpty(config.Environment?.KBPath))
                        Program.Log("[Gateway] WARNING: Environment.KBPath is missing in config.json!");
                    else 
                        Program.Log($"[Gateway] KB Path configured: {config.Environment.KBPath}");

                    string? portOverride = global::System.Environment.GetEnvironmentVariable("GX_MCP_PORT");
                    if (int.TryParse(portOverride, out int httpPortOverride) && httpPortOverride > 0)
                    {
                        config.Server ??= new ServerConfig();
                        config.Server.HttpPort = httpPortOverride;
                        Program.Log($"[Gateway] HTTP port overridden by GX_MCP_PORT={httpPortOverride}");
                    }

                    string? stdioOverride = global::System.Environment.GetEnvironmentVariable("GX_MCP_STDIO");
                    if (bool.TryParse(stdioOverride, out bool mcpStdioOverride))
                    {
                        config.Server ??= new ServerConfig();
                        config.Server.McpStdio = mcpStdioOverride;
                        Program.Log($"[Gateway] MCP stdio overridden by GX_MCP_STDIO={mcpStdioOverride}");
                    }
                        
                    return config;
                }
                catch (IOException)
                {
                    Thread.Sleep(100);
                }
            }
            throw new Exception("Could not read config.json after multiple attempts.");
        }

        private static void SetupWatcher(string path)
        {
            if (_watcher != null) return;

            string dir = Path.GetDirectoryName(path)!;
            string file = Path.GetFileName(path);

            _watcher = new FileSystemWatcher(dir, file);
            _watcher.NotifyFilter = NotifyFilters.LastWrite;
            _watcher.Changed += (s, e) => {
                Program.Log($"[Gateway] Configuration file changed: {e.FullPath}");
                // Add a small delay to ensure writing process has released the lock
                Thread.Sleep(200);
                try {
                    var newConfig = ParseConfig(path);
                    OnConfigurationChanged?.Invoke(newConfig);
                } catch (Exception ex) {
                    Program.Log($"[Gateway] Failed to reload configuration: {ex.Message}");
                }
            };
            _watcher.EnableRaisingEvents = true;
        }
    }

    public class GeneXusConfig
    {
        public string? InstallationPath { get; set; }
        public string? WorkerExecutable { get; set; }
    }

    public class ServerConfig
    {
        public int HttpPort { get; set; } = 5000;
        public bool McpStdio { get; set; } = true;
        public string BindAddress { get; set; } = "127.0.0.1";
        public List<string> AllowedOrigins { get; set; } = new List<string>();
        public int SessionIdleTimeoutMinutes { get; set; } = 10;
        public int WorkerIdleTimeoutMinutes { get; set; } = 5;
        public int IdempotencyTtlMinutes { get; set; } = 15;
        public int IdempotencyCacheSize { get; set; } = 1000;
    }

    public class LoggingConfig
    {
        public string? Level { get; set; }
        public string? Path { get; set; }
    }

    public class EnvironmentConfig
    {
        public string? KBPath { get; set; }
        public string? GX_SHADOW_PATH { get; set; }
    }
}
