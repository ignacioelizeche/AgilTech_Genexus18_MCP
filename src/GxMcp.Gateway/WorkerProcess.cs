using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace GxMcp.Gateway
{
    public class WorkerProcess
    {
        private Process? _process;
        private readonly Configuration _config;
        private readonly SemaphoreSlim _streamLock = new SemaphoreSlim(1, 1);

        public event Action<string>? OnRpcResponse;

        public WorkerProcess(Configuration config)
        {
            _config = config;
        }

        public void Start()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string workerPath = _config.GeneXus?.WorkerExecutable ?? "";
            
            // Handle relative paths in config
            if (!Path.IsPathRooted(workerPath)) workerPath = Path.Combine(baseDir, workerPath);

            if (!File.Exists(workerPath))
            {
                // Fallbacks for dev environment
                string[] devPaths = new[] {
                    Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\..\src\GxMcp.Worker\bin\Debug\GxMcp.Worker.exe")),
                    Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\src\GxMcp.Worker\bin\Debug\GxMcp.Worker.exe")),
                    Path.Combine(baseDir, @"worker\GxMcp.Worker.exe")
                };
                foreach (var p in devPaths) { if (File.Exists(p)) { workerPath = p; break; } }
            }

            if (!File.Exists(workerPath)) throw new FileNotFoundException($"Worker NOT FOUND at {workerPath}");

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = workerPath,
                WorkingDirectory = Path.GetDirectoryName(workerPath) ?? "",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // INJECT KB PATH (Source of truth)
            string kbPath = _config.Environment?.KBPath ?? "";
            if (string.IsNullOrEmpty(kbPath)) 
            {
                Console.Error.WriteLine("[Gateway] CRITICAL: KBPath is empty! Worker will fail.");
            }

            startInfo.Arguments = $"--kb \"{kbPath}\"";
            startInfo.EnvironmentVariables["GX_PROGRAM_DIR"] = _config.GeneXus?.InstallationPath ?? "";
            startInfo.EnvironmentVariables["GX_KB_PATH"] = kbPath;
            startInfo.EnvironmentVariables["PATH"] = (_config.GeneXus?.InstallationPath ?? "") + ";" + Environment.GetEnvironmentVariable("PATH");

            Console.Error.WriteLine($"[Gateway] Spawning Worker with GX_KB_PATH={kbPath}");

            _process = new Process { StartInfo = startInfo };
            
            _process.OutputDataReceived += (sender, e) => {
                if (!string.IsNullOrEmpty(e.Data)) {
                    if (e.Data.TrimStart().StartsWith("{") && e.Data.Contains("\"jsonrpc\"")) OnRpcResponse?.Invoke(e.Data);
                    else Console.Error.WriteLine($"[Worker] {e.Data}");
                }
            };
            
            _process.ErrorDataReceived += (sender, e) => {
                if (!string.IsNullOrEmpty(e.Data)) Console.Error.WriteLine(e.Data);
            };

            _process.Start();
            _process.StandardInput.AutoFlush = true;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public async Task SendCommandAsync(string jsonRpc)
        {
            if (_process == null || _process.HasExited) Start();

            await _streamLock.WaitAsync();
            try {
                await _process.StandardInput.WriteLineAsync(jsonRpc);
                await _process.StandardInput.FlushAsync();
            }
            catch {
                Stop();
                Start();
                await _process!.StandardInput.WriteLineAsync(jsonRpc);
            }
            finally { _streamLock.Release(); }
        }

        public void Stop()
        {
            if (_process != null && !_process.HasExited) {
                _process.Kill();
                _process.Dispose();
            }
        }
    }
}
