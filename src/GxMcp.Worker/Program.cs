using System;
using System.IO;
// using Newtonsoft.Json; // Native .NET 4.8 doesn't have System.Text.Json, will need Newtonsoft or simple string parsing for now to minimize dependencies if we want ultra-light.
// Actually we have a reference to Artech.Genexus.Common, which might use Newtonsoft. 
// But let's stick to simple Console reading for the loop.

namespace GxMcp.Worker
{
    class Program
    {
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

            // Redirect Console.Error to a file for better debugging
            // Redirect Console.Error to a file for better debugging
            string cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
            if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);
            
            var logPath = Path.Combine(cacheDir, "analysis_trace.log");
            try {
                var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var logWriter = new StreamWriter(stream) { AutoFlush = true };
                Console.SetError(logWriter);
            } catch { }

            // 1. Initialize SDK
            string gxPath = @"C:\Program Files (x86)\GeneXus\GeneXus18";
            string currentDir = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(gxPath);
                Console.Error.WriteLine("[Worker] Initializing GeneXus SDK...");
                Console.Error.Flush();
                
                // 1. Disable UI
                var uiServicesType = System.Reflection.Assembly.Load("Artech.Architecture.UI.Framework")
                    .GetType("Artech.Architecture.UI.Framework.Services.UIServices");
                uiServicesType?.GetMethod("SetDisableUI", new Type[] { typeof(bool) })?.Invoke(null, new object[] { true });

                // 2. Connector.Initialize(true)
                var connectorType = System.Reflection.Assembly.Load("Connector").GetType("Artech.Core.Connector");
                var initMethod = connectorType.GetMethod("Initialize", new Type[] { typeof(bool) });
                if (initMethod != null) {
                    initMethod.Invoke(null, new object[] { true });
                } else {
                    connectorType.GetMethod("Initialize", new Type[] { })?.Invoke(null, null);
                }
                
                // 3. Start Business Logic
                connectorType.GetMethod("StartBL", new Type[] { })?.Invoke(null, null);
                
                Console.Error.WriteLine("[Worker] SDK Initialized successfully.");
                Console.Error.Flush();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Worker Critical Error] SDK Init Failed: {ex.Message}");
                if (ex.InnerException != null) Console.Error.WriteLine($"[Worker Critical Error] Inner: {ex.InnerException.Message}");
            }
            finally
            {
                Directory.SetCurrentDirectory(currentDir);
            }
            
            // Note: StartBL might still be needed if ArtechServices.Initialize isn't enough.
            // But let's see if this one hangs.

            // 2. Initialize Services
            var dispatcher = Services.CommandDispatcher.Instance;
            dispatcher.PreWarm();
            
            Console.Error.WriteLine("[Worker] Started. Waiting for commands...");

            string line;
            while ((line = Console.ReadLine()) != null)
            {
                try 
                {
                    // Console.Error.WriteLine($"[Worker] Received: {line}"); // Debug only, too noisy
                    
                    // Dispatch
                    string result = dispatcher.Dispatch(line);
                    string id = dispatcher.GetId(line);
                    
                    // Respond
                    string idJson = id == null ? "null" : $"\"{id}\"";
                    string response = "{\"jsonrpc\": \"2.0\", \"result\": " + result + ", \"id\": " + idJson + "}";
                    Console.WriteLine(response);
                    Console.Out.Flush();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Worker Loop Error] {ex.Message}");
                }
            }
        }

        private static System.Reflection.Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            string shortName = new System.Reflection.AssemblyName(args.Name).Name;
            if (shortName.EndsWith(".resources")) return null;
            
            string name = shortName + ".dll";
            string gx = @"C:\Program Files (x86)\GeneXus\GeneXus18";
            var paths = new System.Collections.Generic.List<string> { 
                gx, 
                System.IO.Path.Combine(gx, "Packages"), 
                System.IO.Path.Combine(gx, "Packages", "Patterns"),
                AppDomain.CurrentDomain.BaseDirectory 
            };
            
            foreach (var p in paths) {
                string f = System.IO.Path.Combine(p, name);
                if (System.IO.File.Exists(f)) {
                    try { 
                        var asm = System.Reflection.Assembly.LoadFrom(f);
                        Console.Error.WriteLine($"[AssemblyResolve] Loaded: {name} from {p}");
                        return asm; 
                    } catch (Exception ex) { 
                        Console.Error.WriteLine($"[AssemblyResolve] Failed to load {name} from {p}: {ex.Message}");
                    }
                }
            }
            // Console.Error.WriteLine($"[AssemblyResolve] Not found: {name}");
            return null;
        }
    }
}
