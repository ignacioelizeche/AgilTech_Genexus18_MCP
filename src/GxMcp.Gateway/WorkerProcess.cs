using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using Newtonsoft.Json;

namespace GxMcp.Gateway
{
    public class WorkerProcess
    {
        private Process? _process;
        private readonly Configuration _config;
        private readonly Channel<string> _commandChannel = Channel.CreateUnbounded<string>();
        private Task? _writerTask;
        private Task? _healthCheckTask;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private DateTime _lastResponse = DateTime.Now;
        private bool _isPinging = false;

        public event Action<string>? OnRpcResponse;
        public event Action? OnWorkerExited;

        public WorkerProcess(Configuration config)
        {
            _config = config;
            _writerTask = Task.Run(ProcessQueueAsync);
        }

        private async Task RunHealthCheckAsync(CancellationToken ct)
        {
            await Task.Delay(5000, ct); // Grace period
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        // Check for long operation: No response for more than 45 seconds
                        if ((DateTime.Now - _lastResponse).TotalSeconds > 45)
                        {
                            Console.Error.WriteLine("[Gateway] Warning: Worker unresponsive for 45s. It may be processing a heavy load or a long KB operation.");
                            // Removed forced restart to prevent aborting heavy operations like Full Indexing
                        }
                        else
                        {
                            // Send a ping
                            Program.Log("[Health] Sending Ping to Worker...");
                            var ping = new { jsonrpc = "2.0", id = "heartbeat", method = "ping" };
                            await SendCommandAsync(JsonConvert.SerializeObject(ping));
                            }
                            }
                            }
                            catch (Exception ex)
                            {
                            Program.Log($"[Health] Error during ping: {ex.Message}");
                            }
                            await Task.Delay(15000, ct);
                            }
                            }

        private async Task ProcessQueueAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (await _commandChannel.Reader.WaitToReadAsync(_cts.Token))
                    {
                        while (_commandChannel.Reader.TryRead(out var jsonRpc))
                        {
                            if (_process == null || _process.HasExited) Start();
                            
                            try {
                                await _process!.StandardInput.WriteLineAsync(jsonRpc);
                                await _process!.StandardInput.FlushAsync();
                            } catch {
                                StopProcess();
                                Start();
                                await _process!.StandardInput.WriteLineAsync(jsonRpc);
                                await _process!.StandardInput.FlushAsync();
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Gateway] Writer Error: {ex.Message}");
                    await Task.Delay(1000); // Backoff
                }
            }
        }

        public void Start()
        {
            if (_process != null && !_process.HasExited) return;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string workerPath = _config.GeneXus?.WorkerExecutable ?? "";
            
            if (!Path.IsPathRooted(workerPath)) workerPath = Path.Combine(baseDir, workerPath);

            if (!File.Exists(workerPath))
            {
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
                StandardInputEncoding = System.Text.Encoding.UTF8,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            string kbPath = _config.Environment?.KBPath ?? "";
            startInfo.Arguments = $"--kb \"{kbPath}\"";
            startInfo.EnvironmentVariables["GX_PROGRAM_DIR"] = _config.GeneXus?.InstallationPath ?? "";
            startInfo.EnvironmentVariables["GX_KB_PATH"] = kbPath;
            startInfo.EnvironmentVariables["GX_SHADOW_PATH"] = _config.Environment?.GX_SHADOW_PATH ?? Path.Combine(kbPath, ".gx_mirror");
            startInfo.EnvironmentVariables["PATH"] = (_config.GeneXus?.InstallationPath ?? "") + ";" + Environment.GetEnvironmentVariable("PATH");

            _process = new Process { StartInfo = startInfo };
            _process.EnableRaisingEvents = true;
            _process.Exited += (s, e) => {
                Console.Error.WriteLine("[Gateway] Worker process EXITED.");
                OnWorkerExited?.Invoke();
            };
            
            _process.OutputDataReceived += (sender, e) => {
                if (!string.IsNullOrEmpty(e.Data)) {
                    _lastResponse = DateTime.Now; // Heartbeat update
                    if (e.Data.TrimStart().StartsWith("{") && e.Data.Contains("\"jsonrpc\"")) OnRpcResponse?.Invoke(e.Data);
                    else {
                        Console.Error.WriteLine($"[Worker] {e.Data}");
                    }
                }
            };
            
            _process.ErrorDataReceived += (sender, e) => {
                if (!string.IsNullOrEmpty(e.Data)) {
                    Console.Error.WriteLine($"[Worker-Err] {e.Data}");
                }
            };

            _process.Start();
            _process.StandardInput.AutoFlush = true;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            if (_healthCheckTask == null || _healthCheckTask.IsCompleted)
            {
                _healthCheckTask = Task.Run(() => RunHealthCheckAsync(_cts.Token));
            }
        }

        public async Task SendCommandAsync(string jsonRpc)
        {
            await _commandChannel.Writer.WriteAsync(jsonRpc);
        }

        public void Stop()
        {
            _cts.Cancel();
            StopProcess();
        }

        private void StopProcess()
        {
            if (_process != null) {
                try { 
                    if (!_process.HasExited) _process.Kill(); 
                    _process.Dispose();
                } catch (Exception ex) { 
                    Program.Log($"[Gateway] Error during process cleanup: {ex.Message}");
                }
                _process = null;
            }
        }
    }
}
