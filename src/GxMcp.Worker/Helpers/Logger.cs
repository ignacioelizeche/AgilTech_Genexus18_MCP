using System;
using System.IO;

namespace GxMcp.Worker.Helpers
{
    public static class Logger
    {
        private static readonly string LogFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "worker_debug.log");
        private static readonly object LockObj = new object();

        public static void Info(string message) => Log("INFO", message);
        public static void Warn(string message) => Log("WARN", message);
        public static void Error(string message) => Log("ERROR", message);
        public static void Debug(string message) => Log("DEBUG", message);

        private static void Log(string level, string message)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string line = $"[{timestamp}] [{level}] {message}";
            
            // Log to Console.Error for Gateway capturing
            Console.Error.WriteLine($"[Worker Log] {line}");

            try
            {
                lock (LockObj)
                {
                    File.AppendAllText(LogFile, line + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Logger File Error] {ex.Message}");
            }
        }
    }
}
