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
            // Redirect Console.Error to a file for better debugging
            string cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
            if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);
            
            // Use FileShare.ReadWrite to allow multiple instances or external readers
            var logPath = Path.Combine(cacheDir, "analysis_trace.log");
            var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var logWriter = new StreamWriter(stream) { AutoFlush = true };
            Console.SetError(logWriter);

            // 1. Initialize Services
            var dispatcher = new Services.CommandDispatcher();
            
            Console.Error.WriteLine("[Worker] Started. Waiting for commands...");

            string line;
            while ((line = Console.ReadLine()) != null)
            {
                try 
                {
                    Console.Error.WriteLine($"[Worker] Received: {line}");
                    
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
                    Console.Error.WriteLine($"[Worker Error] {ex.Message}");
                }
            }
        }
    }
}
