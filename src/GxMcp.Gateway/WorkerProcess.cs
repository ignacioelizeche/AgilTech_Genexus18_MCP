using System;
using System.Management;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.IO.Pipes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
        private TaskCompletionSource<bool> _pipeReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private string _lastOperationInfo = "None";
        private readonly object _processLock = new object();
        private bool _isStarting;

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
                            Program.TryWriteStderr($"[Gateway] Warning: Worker unresponsive for 45s. Last activity: {_lastOperationInfo}. It may be processing a heavy load or a long KB operation.");
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
                            if (string.IsNullOrEmpty(jsonRpc)) continue;

                            if (!IsProcessRunning(_process)) Start();
                            
                            string id = "unknown";
                            try {
                                var json = JsonConvert.DeserializeObject<JObject>(jsonRpc);
                                if (json?["id"] != null) id = json["id"]?.ToString() ?? "unknown";
                                string method = json?["method"]?.ToString() ?? "unknown";
                                _lastOperationInfo = $"{method} (ID: {id})";
                            } catch { /* ignore */ }

                            try {
                                await WaitForPipeReadyAsync(id, _cts.Token);

                                lock (_processLock) {
                                    if (_pipeWriter != null) {
                                        _pipeWriter.WriteLine(jsonRpc);
                                        _pipeWriter.Flush();
                                        Program.Log($"[Gateway] Command written to pipe: {id}");
                                    } else {
                                        Program.Log($"[Gateway] ERROR: Cannot send command {id}, pipe not available after wait.");
                                    }
                                }
                            } catch (Exception ex) {
                                Program.Log($"[Gateway] IPC Send Error ({id}): {ex.Message}");
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Program.Log($"[Gateway] Critical Error in ProcessQueueAsync: {ex.Message}");
                    try { await Task.Delay(1000, _cts.Token); } catch { break; }
                }
            }
        }

        private static bool IsProcessRunning(Process? process)
        {
            if (process == null) return false;
            try
            {
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private async Task WaitForPipeReadyAsync(string id, CancellationToken cancellationToken)
        {
            Task pipeReadyTask;
            lock (_processLock)
            {
                if (_pipeWriter != null)
                {
                    return;
                }

                pipeReadyTask = _pipeReady.Task;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            var cancellationTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
            var completed = await Task.WhenAny(pipeReadyTask, cancellationTask);
            if (completed != pipeReadyTask)
            {
                throw new TimeoutException($"Worker pipe was not ready in time for command {id}.");
            }
        }

        public static void KillOrphanGateways(string? kbPath = null)
        {
            try
            {
                int currentPid = Environment.ProcessId;
                string[] targets = { "GxMcp.Gateway", "GxMcp.Worker" };

                foreach (var name in targets)
                {
                    foreach (var proc in Process.GetProcessesByName(name))
                    {
                        if (proc.Id == currentPid) continue;
                        try
                        {
                            Program.Log($"[Gateway] Killing orphan {name} (PID {proc.Id})...");
                            proc.Kill(true);
                            proc.WaitForExit(3000);
                            System.Threading.Thread.Sleep(200);
                        }
                        catch { }
                    }
                }

                // Selective dotnet kill
                foreach (var proc in Process.GetProcessesByName("dotnet"))
                {
                    if (proc.Id == currentPid) continue;
                    try {
                        string cmdLine = GetCommandLine(proc);
                        if (string.IsNullOrEmpty(cmdLine)) continue;

                        bool isOurs = cmdLine.Contains("GxMcp.Gateway.dll", StringComparison.OrdinalIgnoreCase) || 
                                     cmdLine.Contains("GxMcp.Worker.dll", StringComparison.OrdinalIgnoreCase);
                        
                        if (isOurs) {
                            Program.Log($"[Gateway] Killing orphan dotnet-mcp (PID {proc.Id}, Cmd: {cmdLine})...");
                            proc.Kill(true);
                            proc.WaitForExit(3000);
                            System.Threading.Thread.Sleep(200);
                        }
                    } catch { }
                }
            }
            catch { }
        }

        private static void KillOrphanWorkers()
        {
            // Now handled by KillOrphanGateways which is called earlier and more broadly
            // but keeping empty or calling it for compatibility if needed.
            // KillOrphanGateways(); 
        }

        private static string GetCommandLine(Process process)
        {
            try {
                using (var searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + process.Id))
                using (var objects = searcher.Get())
                {
                    foreach (var obj in objects)
                    {
                        return obj["CommandLine"]?.ToString() ?? "";
                    }
                }
            } catch { }
            return "";
        }

        public void Start()
        {
            lock (_processLock)
            {
                if (_isStarting || IsProcessRunning(_process))
                {
                    return;
                }

                _isStarting = true;
            }

            try
            {
                KillOrphanWorkers();
                _pipeReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

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
                    int exitCode = -1;
                    try { exitCode = _process.ExitCode; } catch { }
                    Program.Log($"[Gateway] Worker process EXITED with code {exitCode}.");
                    OnWorkerExited?.Invoke();
                    if (!_cts.Token.IsCancellationRequested)
                    {
                        Task.Delay(2000, _cts.Token).ContinueWith(_ =>
                        {
                            if (!_cts.Token.IsCancellationRequested && (_process == null || _process.HasExited))
                            {
                                Program.Log("[Gateway] Auto-restarting Worker after crash...");
                                try { Start(); }
                                catch (Exception ex) { Program.Log($"[Gateway] Failed to auto-restart: {ex.Message}"); }
                            }
                        }, TaskContinuationOptions.OnlyOnRanToCompletion);
                    }
                };

                for (int attempt = 1; attempt <= 10; attempt++)
                {
                    try
                    {
                        _process.Start();
                        Program.Log($"[Gateway] Worker started successfully (Attempt {attempt}).");
                        break;
                    }
                    catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
                    {
                        Program.Log($"[Gateway] Access denied (5) when starting worker. Attempt {attempt}/10. File might be locked. Retrying in 1s...");
                        if (attempt == 10) throw;
                        System.Threading.Thread.Sleep(1000);
                    }
                }

                _process.OutputDataReceived += (sender, e) => {
                    if (!string.IsNullOrEmpty(e.Data)) {
                        _lastResponse = DateTime.Now; // Heartbeat update
                        if (e.Data.TrimStart().StartsWith("{") && e.Data.Contains("\"jsonrpc\"")) OnRpcResponse?.Invoke(e.Data);
                        else {
                            Program.TryWriteStderr($"[Worker] {e.Data}");
                        }
                    }
                };
                
                _process.ErrorDataReceived += (sender, e) => {
                    if (!string.IsNullOrEmpty(e.Data)) {
                        _lastResponse = DateTime.Now; // Heartbeat update from logs
                        Program.TryWriteStderr($"[Worker-Err] {e.Data}");
                    }
                };

                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                _pipeWriter = _process.StandardInput;
                _pipeWriter.AutoFlush = true;
                Program.Log("[Gateway] Worker stdio command channel initialized.");
                _pipeReady.TrySetResult(true);

                if (_healthCheckTask == null || _healthCheckTask.IsCompleted)
                {
                    _healthCheckTask = Task.Run(() => RunHealthCheckAsync(_cts.Token));
                }
            }
            finally
            {
                lock (_processLock)
                {
                    _isStarting = false;
                }
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
            lock (_processLock) {
                if (_pipeWriter != null) { try { _pipeWriter.Dispose(); } catch { } _pipeWriter = null; }
                if (_pipeReader != null) { try { _pipeReader.Dispose(); } catch { } _pipeReader = null; }
                if (_pipeServer != null) { try { _pipeServer.Dispose(); } catch { } _pipeServer = null; }
                _pipeReady.TrySetCanceled();

                if (_process != null) {
                    try { 
                        if (!_process.HasExited) _process.Kill(true); 
                        _process.Dispose();
                    } catch (Exception ex) { 
                        Program.Log($"[Gateway] Error during process cleanup: {ex.Message}");
                    }
                    _process = null;
                }
            }
        }
    }
}
