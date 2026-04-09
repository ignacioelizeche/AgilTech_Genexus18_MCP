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
        private static readonly BlockingCollection<string> _outputQueue = new BlockingCollection<string>();
        private static readonly BlockingCollection<string> _errorQueue = new BlockingCollection<string>();
        private static CommandDispatcher _dispatcher;
        private static TextWriter _originalOut;
        private static TextWriter _originalError;
        private static StreamWriter _pipeWriter;

        [STAThread]
        static void Main(string[] args)
        {
            try {
                // Force UTF-8 on worker stdio before capturing the original writers.
                Console.InputEncoding = System.Text.Encoding.UTF8;
                Console.OutputEncoding = new System.Text.UTF8Encoding(false);

                // ELITE: Start output threads first to capture all logs immediately
                StartOutputThreads();
                Console.SetOut(new QueueWriter(_outputQueue));
                Console.SetError(new QueueWriter(_errorQueue));

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

                WriteLine("WORKER_HANDSHAKE_START");
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

                string pipeName = Environment.GetEnvironmentVariable("GX_MCP_PIPE");
                if (!string.IsNullOrEmpty(pipeName))
                {
                    try {
                        var pipeClient = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                        pipeClient.Connect(30000);
                        var writer = new StreamWriter(pipeClient, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                        var reader = new StreamReader(pipeClient, new System.Text.UTF8Encoding(false));
                        
                        _pipeWriter = writer;
                        Console.SetIn(reader);
                        Logger.Info($"[Worker] Connected to IPC Pipe {pipeName} successfully.");
                    } catch (Exception ex) {
                        Logger.Error($"[Worker] IPC Pipe Connection Error: {ex.Message}. Falling back to STDIO.");
                    }
                }

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

                // Start External KB Watcher
                var watcher = new KbWatcherService(_dispatcher.GetKbService(), (name, type, time) => {
                    SendNotification("notifications/resources/updated", new {
                        name = name,
                        type = type,
                        updatedAt = time,
                        external = true
                    });
                });
                watcher.Start();

                var readerThread = new Thread(() => {
                    while (true) {
                        string line = Console.ReadLine();
                        if (line == null) break;
                        if (line.Trim().Equals("ping", StringComparison.OrdinalIgnoreCase) || line.Contains("\"method\":\"ping\"") || line.Contains("\"action\":\"Ping\""))
                        {
                            WriteLine("{\"jsonrpc\":\"2.0\",\"result\":{\"status\":\"Ready\"},\"id\":\"heartbeat\"}");
                            if (!line.Contains("\"method\"")) continue;
                        }
                        if (!string.IsNullOrWhiteSpace(line)) CommandQueue.Add(line);
                    }
                    CommandQueue.CompleteAdding();
                }) { IsBackground = true, Name = "HeartbeatReader" };
                readerThread.Start();

                // DEDICATED SDK WORKER THREAD (STA)
                var sdkWorker = new Thread(() => {
                    Logger.Info("SDK Worker Thread started.");
                    foreach (var line in SdkCommandQueue.GetConsumingEnumerable())
                    {
                        ProcessCommand(line);
                    }
                }) { IsBackground = true, Name = "SdkWorker", Priority = ThreadPriority.AboveNormal };
                sdkWorker.SetApartmentState(ApartmentState.STA);
                sdkWorker.Start();

                // DEDICATED STA BACKGROUND TASK THREAD
                var backgroundWorker = new Thread(() => {
                    Logger.Info("Background STA Worker Thread started.");
                    while (!CommandQueue.IsCompleted) {
                        if (BackgroundQueue.TryDequeue(out var action)) {
                            try { action(); }
                            catch (Exception ex) { Logger.Error("Background Task Error: " + ex.Message); }
                        } else {
                            Thread.Sleep(100);
                        }
                    }
                }) { IsBackground = true, Name = "BackgroundWorker" };
                backgroundWorker.SetApartmentState(ApartmentState.STA);
                backgroundWorker.Start();

                // MAIN DISPATCHER LOOP
                while (!CommandQueue.IsCompleted || CommandQueue.Count > 0)
                {
                    if (CommandQueue.TryTake(out string line, 100))
                    {
                        if (_dispatcher.IsThreadSafe(line))
                            System.Threading.Tasks.Task.Run(() => ProcessCommand(line));
                        else
                            SdkCommandQueue.Add(line);
                    }
                }

                Logger.Info("Input EOF reached. Shutting down...");
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
                
                var archAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Artech.Architecture.Common.dll"));
                var contextServiceType = archAsm.GetType("Artech.Architecture.Common.Services.ContextService");
                contextServiceType?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);

                var blAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Artech.Architecture.BL.Framework.dll"));
                var blCommonType = blAsm.GetType("Artech.Architecture.BL.Framework.Services.CommonServices");
                blCommonType?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);

                var uiAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Artech.Architecture.UI.Framework.dll"));
                var uiType = uiAsm.GetType("Artech.Architecture.UI.Framework.Services.UIServices");
                uiType?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);

                var commonAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Artech.Genexus.Common.dll"));
                var initType = commonAsm.GetType("Artech.Genexus.Common.KBModelObjectsInitializer");
                initType?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);

                var connAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Connector.dll"));
                var connType = connAsm.GetType("Artech.Core.Connector");
                connType?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                connType?.GetMethod("Start", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                
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
                var obj = JObject.Parse(line);
                string idJson = obj["id"]?.ToString() ?? "null";
                string method = obj["method"]?.ToString();
                string correlationId = obj["params"]?["correlationId"]?.ToString() ?? "n/a";
                Logger.Info($"[WORKER] Command: {method} ({idJson}) [cid:{correlationId}]");
                string result = _dispatcher.Dispatch(line);
                SendResponse(result, idJson);
            } catch (Exception ex) { Logger.Error("ProcessCommand Error: " + ex.Message); }
        }

        private static void SendResponse(string result, string id)
        {
            try {
                object resultObj;
                try { resultObj = JToken.Parse(result); } catch { resultObj = result; }
                var response = new { jsonrpc = "2.0", result = resultObj, id = id };
                WriteLine(JsonConvert.SerializeObject(response, Formatting.None));
            } catch (Exception ex) { Logger.Error("SendResponse Error: " + ex.Message); }
        }

        public static void SendNotification(string method, object @params)
        {
            try {
                var notification = new { jsonrpc = "2.0", method = method, @params = @params };
                WriteLine(JsonConvert.SerializeObject(notification, Formatting.None));
            } catch (Exception ex) { Logger.Error("SendNotification Error: " + ex.Message); }
        }

        public static void WriteLine(string line) => _outputQueue.Add(line);
        public static void WriteError(string line) => _errorQueue.Add(line);

        private static void StartOutputThreads()
        {
            _originalOut = Console.Out;
            _originalError = Console.Error;

            var outThread = new Thread(() => {
                foreach (var line in _outputQueue.GetConsumingEnumerable()) {
                    try { 
                        if (_pipeWriter != null) {
                            _pipeWriter.WriteLine(line);
                            _pipeWriter.Flush();
                        } else {
                            _originalOut.WriteLine(line); 
                            _originalOut.Flush(); 
                        }
                    } catch { }
                }
            }) { IsBackground = true, Name = "OutputWriter" };
            outThread.Start();

            var errThread = new Thread(() => {
                foreach (var line in _errorQueue.GetConsumingEnumerable()) {
                    try { _originalError.WriteLine(line); _originalError.Flush(); } catch { }
                }
            }) { IsBackground = true, Name = "ErrorWriter" };
            errThread.Start();
        }

        private static JObject LoadLocalConfig()
        {
            try {
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string configPath = Path.Combine(exeDir, "config.json");
                if (File.Exists(configPath)) return JObject.Parse(File.ReadAllText(configPath));
            } catch { }
            return null;
        }
    }

    public class QueueWriter : TextWriter
    {
        private readonly BlockingCollection<string> _queue;
        private readonly System.Text.StringBuilder _buffer = new System.Text.StringBuilder();

        public QueueWriter(BlockingCollection<string> queue) { _queue = queue; }
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                lock (_buffer) {
                    _queue.Add(_buffer.ToString());
                    _buffer.Clear();
                }
            }
            else if (value != '\r')
            {
                lock (_buffer) { _buffer.Append(value); }
            }
        }

        public override void Write(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            foreach (char c in value) Write(c);
        }

        public override void WriteLine(string value)
        {
            Write(value);
            Write('\n');
        }
    }
}
