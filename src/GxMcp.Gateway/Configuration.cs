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

            Console.Error.WriteLine($"[Gateway] Loading config from: {CurrentConfigPath}");
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
                        Console.Error.WriteLine("[Gateway] WARNING: Environment.KBPath is missing in config.json!");
                    else 
                        Console.Error.WriteLine($"[Gateway] KB Path configured: {config.Environment.KBPath}");
                        
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
                Console.Error.WriteLine($"[Gateway] Configuration file changed: {e.FullPath}");
                // Add a small delay to ensure writing process has released the lock
                Thread.Sleep(200);
                try {
                    var newConfig = ParseConfig(path);
                    OnConfigurationChanged?.Invoke(newConfig);
                } catch (Exception ex) {
                    Console.Error.WriteLine($"[Gateway] Failed to reload configuration: {ex.Message}");
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
