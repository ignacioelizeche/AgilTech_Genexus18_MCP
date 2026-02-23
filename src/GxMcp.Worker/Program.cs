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
                
                // Explicitly open KB if path is provided in environment or arguments
                string kbPath = Environment.GetEnvironmentVariable("GX_KB_PATH");
                
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
                
                // 1. Initialize UI Services (Bridge mode)
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
                var connType = connAsm.GetType("Artech.Core.Connector");
                connType?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                connType?.GetMethod("Start", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                
                var archAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Artech.Architecture.Common.dll"));
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
    }
}
