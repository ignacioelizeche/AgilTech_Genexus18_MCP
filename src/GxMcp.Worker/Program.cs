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
        private static readonly BlockingCollection<string> CommandQueue = new BlockingCollection<string>();
        private static CommandDispatcher _dispatcher;

        [STAThread]
        static void Main(string[] args)
        {
            try {
                AppDomain.CurrentDomain.UnhandledException += (s, e) => {
                    Logger.Error("FATAL: " + (e.ExceptionObject as Exception)?.Message);
                };

                Console.WriteLine("WORKER_HANDSHAKE_START");
                Logger.Info("Worker process started (STA Mode).");

                string gxPath = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR") ?? @"C:\Program Files (x86)\GeneXus\GeneXus18";
                
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
                Logger.Info("Worker SDK ready.");

                var readerThread = new Thread(() => {
                    using (var reader = new StreamReader(Console.OpenStandardInput())) {
                        while (true) {
                            string line = reader.ReadLine();
                            if (line == null) break;
                            if (line.Trim().Equals("ping", StringComparison.OrdinalIgnoreCase))
                            {
                                lock (Console.Out) { Console.WriteLine("{\"jsonrpc\":\"2.0\",\"result\":\"pong\",\"id\":\"heartbeat\"}"); Console.Out.Flush(); }
                                continue;
                            }
                            if (!string.IsNullOrWhiteSpace(line)) CommandQueue.Add(line);
                        }
                    }
                    CommandQueue.CompleteAdding();
                }) { IsBackground = true, Name = "HeartbeatReader" };
                readerThread.Start();

                foreach (string line in CommandQueue.GetConsumingEnumerable())
                {
                    ProcessCommand(line);
                }
            } catch (Exception ex) {
                Logger.Error($"Main FATAL: {ex.Message}");
            }
        }

        private static void InitializeSdk(string gxPath)
        {
            try {
                Logger.Debug($"Setting current directory to {gxPath}");
                Directory.SetCurrentDirectory(gxPath);
                
                Logger.Debug("Loading Connector.dll...");
                var connAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Connector.dll"));
                var connType = connAsm.GetType("Artech.Core.Connector");
                Logger.Debug("Initializing Connector...");
                connType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
                Logger.Debug("Starting Connector...");
                connType.GetMethod("Start", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);

                Logger.Debug("Loading UI Framework...");
                var uiAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Artech.Architecture.UI.Framework.dll"));
                var uiType = uiAsm.GetType("Artech.Architecture.UI.Framework.Services.UIServices");
                if (uiType != null)
                {
                    var setDisableMethod = uiType.GetMethod("SetDisableUI", BindingFlags.Public | BindingFlags.Static);
                    if (setDisableMethod != null)
                    {
                        Logger.Debug("Disabling UI...");
                        setDisableMethod.Invoke(null, new object[] { true });
                    }
                    else Logger.Warn("Method SetDisableUI not found.");

                    var initUiMethod = uiType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
                    if (initUiMethod != null)
                    {
                        Logger.Debug("Initializing UI Services...");
                        initUiMethod.Invoke(null, null);
                    }
                    else Logger.Warn("Method Initialize (UI) not found.");
                }
                else Logger.Warn("UIServices type not found.");

                Logger.Debug("Loading Genexus Common...");
                var commonAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Artech.Genexus.Common.dll"));
                var initType = commonAsm.GetType("Artech.Genexus.Common.KBModelObjectsInitializer");
                if (initType != null)
                {
                    var initMethod = initType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
                    if (initMethod != null)
                    {
                        Logger.Debug("Initializing KBModelObjects...");
                        initMethod.Invoke(null, null);
                    }
                    else Logger.Warn("Method Initialize (KBModelObjects) not found.");
                }
                else Logger.Warn("KBModelObjectsInitializer type not found.");

                Logger.Debug("Loading Architecture Common...");
                var kbAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Artech.Architecture.Common.dll"));
                var kbType = kbAsm.GetType("Artech.Architecture.Common.Objects.KnowledgeBase");
                if (kbType != null)
                {
                    var kbFactoryProp = kbType.GetProperty("KBFactory", BindingFlags.Public | BindingFlags.Static);
                    if (kbFactoryProp != null)
                    {
                        var factoryType = connAsm.GetType("Connector.KBFactory");
                        if (factoryType != null)
                        {
                            Logger.Debug("Setting KBFactory...");
                            kbFactoryProp.SetValue(null, Activator.CreateInstance(factoryType));
                        }
                        else Logger.Warn("Connector.KBFactory type not found.");
                    }
                    else Logger.Warn("Property KBFactory not found.");
                }
                else Logger.Warn("KnowledgeBase type not found.");
                
                Logger.Info("Surgical Init Success.");
            } catch (Exception ex) { 
                Logger.Error("Init Error: " + ex.Message); 
                if (ex.InnerException != null) Logger.Error("Inner Error: " + ex.InnerException.Message);
            }
        }

        private static void ProcessCommand(string line)
        {
            try {
                var obj = JObject.Parse(line);
                string idJson = obj["id"]?.ToString() ?? "null";

                string result = _dispatcher.Dispatch(line);
                SendResponse(result, idJson);
            } catch (Exception ex) { Logger.Error("ProcessCommand Error: " + ex.Message); }
        }

        private static void SendResponse(string result, string id)
        {
            try {
                // CRITICAL: Ensure result is treated as a raw JToken if it's already JSON
                // to avoid double escaping, then serialize to ONE LINE.
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
    }
}
