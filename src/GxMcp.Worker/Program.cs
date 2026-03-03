using System;
using System.IO;
using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using GxMcp.Worker.Services;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace GxMcp.Worker
{
    class Program
    {
        public static readonly BlockingCollection<string> CommandQueue = new BlockingCollection<string>();
        public static readonly BlockingCollection<string> SdkCommandQueue = new BlockingCollection<string>();
        public static readonly ConcurrentQueue<Action> BackgroundQueue = new ConcurrentQueue<Action>();
        private static CommandDispatcher _dispatcher;

        [STAThread]
        static void Main(string[] args)
        {
            try {
                // Use UTF-8 for communication with Gateway
                // The SDK handles KB encoding internally.
                Console.InputEncoding = System.Text.Encoding.UTF8;
                Console.OutputEncoding = System.Text.Encoding.UTF8;

                // Ensure culture is Portuguese-Brazil for SDK character mapping
                try {
                    System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("pt-BR");
                    System.Threading.Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo("pt-BR");
                } catch { }

                AppDomain.CurrentDomain.UnhandledException += (s, e) => {
                    Logger.Error("FATAL UNHANDLED EXCEPTION: " + (e.ExceptionObject as Exception)?.ToString());
                };

                System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) => {
                    Logger.Error("UNOBSERVED TASK EXCEPTION: " + e.Exception?.ToString());
                    e.SetObserved();
                };

                Console.WriteLine("WORKER_HANDSHAKE_START");
                Logger.Info("Worker process started (STA Mode).");

                // ELITE: Configuration Resolve Logic (Env > Local Config > Error)
                string gxPath = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR");
                string kbPath = Environment.GetEnvironmentVariable("GX_KB_PATH");

                if (string.IsNullOrEmpty(gxPath) || string.IsNullOrEmpty(kbPath))
                {
                    var config = LoadLocalConfig();
                    if (config != null)
                    {
                        if (string.IsNullOrEmpty(gxPath)) gxPath = config["InstallationPath"]?.ToString();
                        if (string.IsNullOrEmpty(kbPath)) kbPath = config["KBPath"]?.ToString();
                    }
                }

                if (string.IsNullOrEmpty(gxPath)) 
                    throw new Exception("GX_PROGRAM_DIR not specified in environment or local config.json.");

                AppDomain.CurrentDomain.AssemblyResolve += (sender, resolveArgs) => {
                    try {
                        string assemblyName = new AssemblyName(resolveArgs.Name).Name + ".dll";
                        string assemblyPath = Path.Combine(gxPath, assemblyName);
                        if (File.Exists(assemblyPath)) return Assembly.LoadFrom(assemblyPath);
                    } catch { }
                    return null;
                };

                InitializeSdk(gxPath);
                _dispatcher = CommandDispatcher.Instance;
                
                // Check command line arguments for --kb
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--kb" && i + 1 < args.Length)
                    {
                        kbPath = args[i + 1];
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(kbPath))
                {
                    try {
                        Logger.Info($"Worker auto-opening KB: {kbPath}");
                        _dispatcher.GetKbService().OpenKB(kbPath);
                    } catch (Exception ex) {
                        Logger.Error($"Worker failed to auto-open KB: {ex.Message}");
                    }
                }

                Logger.Info("Worker SDK ready.");

                var readerThread = new Thread(() => {
                    using (var reader = new StreamReader(Console.OpenStandardInput())) {
                        while (true) {
                            string line = reader.ReadLine();
                            if (line == null) break;
                            if (line.Trim().Equals("ping", StringComparison.OrdinalIgnoreCase) || line.Contains("\"method\":\"ping\"") || line.Contains("\"action\":\"Ping\""))
                            {
                                lock (Console.Out) { Console.WriteLine("{\"jsonrpc\":\"2.0\",\"result\":{\"status\":\"Ready\"},\"id\":\"heartbeat\"}"); Console.Out.Flush(); }
                                if (!line.Contains("\"method\"")) continue; // Only skip if it was a literal ping, leave JSON for full dispatch just in case
                            }
                            if (!string.IsNullOrWhiteSpace(line)) CommandQueue.Add(line);
                        }
                    }
                    CommandQueue.CompleteAdding();
                }) { IsBackground = true, Name = "HeartbeatReader" };
                readerThread.Start();

                // DEDICATED SDK WORKER THREAD (STA)
                // This thread handles all non-thread-safe commands (Write, Save, etc.)
                var sdkWorker = new Thread(() => {
                    Logger.Info("SDK Worker Thread started.");
                    foreach (var line in SdkCommandQueue.GetConsumingEnumerable())
                    {
                        ProcessCommand(line);
                    }
                }) { IsBackground = true, Name = "SdkWorker", Priority = ThreadPriority.AboveNormal };
                sdkWorker.SetApartmentState(ApartmentState.STA);
                sdkWorker.Start();

                // MAIN DISPATCHER LOOP (Ultra-responsive)
                while (!CommandQueue.IsCompleted || CommandQueue.Count > 0)
                {
                    if (CommandQueue.TryTake(out string line, 50))
                    {
                        if (_dispatcher.IsThreadSafe(line))
                        {
                            // SEARCH, HEALTH, etc: Run in parallel immediately
                            System.Threading.Tasks.Task.Run(() => ProcessCommand(line));
                        }
                        else
                        {
                            // WRITE, SDK: Send to sequential SDK Worker
                            SdkCommandQueue.Add(line);
                        }
                    }
                    else
                    {
                        // Process background tasks (only on main thread if safe, otherwise could be delegated)
                        if (BackgroundQueue.TryDequeue(out var action))
                        {
                            try { action(); }
                            catch (Exception ex) { Logger.Error("Background Task Error: " + ex.Message); }
                        }
                    }
                }

                // Wait for Sdk Worker to finish its queue
                Logger.Info("Input EOF reached. Waiting for Background tasks and SdkWorker to finish...");
                
                // Drain background queue first (since it might add to Sdk queue)
                while (BackgroundQueue.Count > 0 || (bool)(_dispatcher.GetKbService().GetType().GetField("_isIndexing", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) ?? false))
                {
                    if (BackgroundQueue.TryDequeue(out var action))
                    {
                        try { action(); }
                        catch (Exception ex) { Logger.Error("Shutdown Background Task Error: " + ex.Message); }
                    }
                    else
                    {
                        Thread.Sleep(100);
                    }
                }

                SdkCommandQueue.CompleteAdding();
                while (!SdkCommandQueue.IsCompleted || SdkCommandQueue.Count > 0)
                {
                    Thread.Sleep(50);
                }
                Logger.Info("Worker shutting down safely.");
            } catch (Exception ex) {
                Logger.Error($"Main FATAL: {ex.Message}");
            }
        }

        private static void InitializeSdk(string gxPath)
        {
            try {
                Logger.Debug($"Setting current directory to {gxPath}");
                Directory.SetCurrentDirectory(gxPath);
                
                // 1. Initialize Context and Services (Critical for KB.Open)
                var archAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Artech.Architecture.Common.dll"));
                var contextServiceType = archAsm.GetType("Artech.Architecture.Common.Services.ContextService");
                contextServiceType?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                Logger.Debug("ContextService Initialized.");

                // Load BL Framework (Critical for implementations)
                var blAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Artech.Architecture.BL.Framework.dll"));
                var blCommonType = blAsm.GetType("Artech.Architecture.BL.Framework.Services.CommonServices");
                blCommonType?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                Logger.Debug("BL Framework CommonServices Initialized.");

                // 2. Initialize UI Services (Bridge mode)
                var uiAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Artech.Architecture.UI.Framework.dll"));
                var uiType = uiAsm.GetType("Artech.Architecture.UI.Framework.Services.UIServices");
                uiType?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                Logger.Debug("UIServices Initialized.");

                // 2. Initialize KB Model Objects (Critical)
                var commonAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Artech.Genexus.Common.dll"));
                var initType = commonAsm.GetType("Artech.Genexus.Common.KBModelObjectsInitializer");
                initType?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                Logger.Debug("KBModelObjects Initialized.");

                // 3. Initialize Connector and Factory
                Logger.Debug("Loading Connector.dll...");
                var connAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Connector.dll"));
                Logger.Debug("Connector.dll loaded.");
                var connType = connAsm.GetType("Artech.Core.Connector");
                Logger.Debug("Invoking Connector.Initialize...");
                connType?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                Logger.Debug("Invoking Connector.Start...");
                connType?.GetMethod("Start", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                Logger.Debug("Connector Started.");
                
                var kbBaseType = archAsm.GetType("Artech.Architecture.Common.Objects.KnowledgeBase");
                var factoryProp = kbBaseType?.GetProperty("KBFactory", BindingFlags.Public | BindingFlags.Static);
                if (factoryProp != null) {
                    var factoryType = connAsm.GetType("Connector.KBFactory");
                    if (factoryType != null) {
                        factoryProp.SetValue(null, Activator.CreateInstance(factoryType));
                        Logger.Info("KBFactory Linked successfully.");
                    }
                }
                
                Logger.Info("Full SDK Initialization SUCCESS.");
            } catch (Exception ex) { 
                Logger.Error("CRITICAL Init Error: " + ex.Message); 
            }
        }

        private static void ProcessCommand(string line)
        {
            try {
                Logger.Debug($"[WORKER-STDI] Received raw line: {(line.Length > 100 ? line.Substring(0, 100) + "..." : line)}");
                var obj = JObject.Parse(line);
                string idJson = obj["id"]?.ToString() ?? "null";
                string method = obj["method"]?.ToString();

                Logger.Info($"[WORKER-STDI] Processing command: {method} (ID: {idJson})");

                string result = _dispatcher.Dispatch(line);
                SendResponse(result, idJson);
            } catch (Exception ex) { Logger.Error("ProcessCommand Error: " + ex.Message + " | Line: " + (line.Length > 100 ? line.Substring(0, 100) : line)); }
        }

        private static void SendResponse(string result, string id)
        {
            try {
                object resultObj;
                try { resultObj = JToken.Parse(result); }
                catch { resultObj = result; }

                var response = new {
                    jsonrpc = "2.0",
                    result = resultObj,
                    id = id
                };

                string json = JsonConvert.SerializeObject(response, Formatting.None);
                
                lock (Console.Out) { 
                    Console.WriteLine(json); 
                    Console.Out.Flush(); 
                }
            } catch (Exception ex) { Logger.Error("SendResponse Error: " + ex.Message); }
        }

        private static JObject LoadLocalConfig()
        {
            try
            {
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string configPath = Path.Combine(exeDir, "config.json");
                if (File.Exists(configPath))
                {
                    return JObject.Parse(File.ReadAllText(configPath));
                }
            }
            catch { }
            return null;
        }
    }
}
