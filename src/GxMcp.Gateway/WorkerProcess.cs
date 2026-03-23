using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.IO.Pipes;
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
        private NamedPipeServerStream? _pipeServer;
        private StreamReader? _pipeReader;
        private StreamWriter? _pipeWriter;

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
                            try
                            {
                                var ping = new { jsonrpc = "2.0", id = "heartbeat", method = "ping" };
                                await SendCommandAsync(JsonConvert.SerializeObject(ping));
                            }
                            catch (Exception exPing)
                            {
                                Program.Log($"[Health] Error sending ping: {exPing.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"[Health] Error during health check loop: {ex.Message}");
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
                                if (_pipeWriter != null) {
                                    await _pipeWriter.WriteLineAsync(jsonRpc);
                                } else {
                                    await _process!.StandardInput.WriteLineAsync(jsonRpc);
                                    await _process!.StandardInput.FlushAsync();
                                }
                            } catch {
                                StopProcess();
                                Start();
                                if (_pipeWriter != null) {
                                    await _pipeWriter.WriteLineAsync(jsonRpc);
                                } else {
                                    await _process!.StandardInput.WriteLineAsync(jsonRpc);
                                    await _process!.StandardInput.FlushAsync();
                                }
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
            string pipeName = "GxMcpPipe_" + Guid.NewGuid().ToString("N");
            startInfo.Arguments = $"--kb \"{kbPath}\"";
            startInfo.EnvironmentVariables["GX_PROGRAM_DIR"] = _config.GeneXus?.InstallationPath ?? "";
            startInfo.EnvironmentVariables["GX_KB_PATH"] = kbPath;
            startInfo.EnvironmentVariables["GX_SHADOW_PATH"] = _config.Environment?.GX_SHADOW_PATH ?? Path.Combine(kbPath, ".gx_mirror");
            startInfo.EnvironmentVariables["GX_MCP_PIPE"] = pipeName;
            startInfo.EnvironmentVariables["PATH"] = (_config.GeneXus?.InstallationPath ?? "") + ";" + Environment.GetEnvironmentVariable("PATH");

            _pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

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

            // Wait for Pipe Connection with timeout
            try {
                var connectTask = _pipeServer.WaitForConnectionAsync(_cts.Token);
                if (connectTask.Wait(10000)) { 
                    Program.Log($"[Gateway] IPC Pipe {pipeName} connected.");
                    _pipeReader = new StreamReader(_pipeServer, System.Text.Encoding.UTF8);
                    _pipeWriter = new StreamWriter(_pipeServer, System.Text.Encoding.UTF8) { AutoFlush = true };
                    
                    _ = Task.Run(async () => {
                        try {
                            while (!_cts.Token.IsCancellationRequested) {
                                string? line = await _pipeReader.ReadLineAsync();
                                if (line == null) break;
                                
                                _lastResponse = DateTime.Now;
                                if (line.TrimStart().StartsWith("{") && line.Contains("\"jsonrpc\"")) {
                                    OnRpcResponse?.Invoke(line);
                                } else {
                                    Console.Error.WriteLine($"[Worker-Pipe] {line}");
                                }
                            }
                        } catch (Exception ex) {
                            if (!_cts.Token.IsCancellationRequested) Program.Log($"[Gateway] IPC Pipe Error: {ex.Message}");
                        }
                    });
                } else {
                    Program.Log($"[Gateway] IPC Pipe failed to connect within 10s. Falling back to Native STDIO.");
                }
            } catch (Exception ex) {
                Program.Log($"[Gateway] IPC Pipe Init Error: {ex.Message}");
            }

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
            if (_pipeWriter != null) { try { _pipeWriter.Dispose(); } catch { } _pipeWriter = null; }
            if (_pipeReader != null) { try { _pipeReader.Dispose(); } catch { } _pipeReader = null; }
            if (_pipeServer != null) { try { _pipeServer.Dispose(); } catch { } _pipeServer = null; }

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
