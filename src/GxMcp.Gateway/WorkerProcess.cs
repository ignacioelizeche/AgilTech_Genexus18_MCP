using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Management;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    public class WorkerProcess
    {
        private Process? _process;
        private readonly Configuration _config;
        private readonly Channel<string> _commandChannel = Channel.CreateUnbounded<string>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _processLock = new object();
        private readonly TimeSpan _workerIdleTimeout;
        private Task? _writerTask;
        private Task? _healthCheckTask;
        private DateTime _lastResponse = DateTime.UtcNow;
        private DateTime _lastActivityUtc = DateTime.UtcNow;
        private NamedPipeServerStream? _pipeServer;
        private StreamReader? _pipeReader;
        private StreamWriter? _pipeWriter;
        private TaskCompletionSource<bool> _pipeReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private string _lastOperationInfo = "None";
        private bool _isStarting;
        private bool _suppressAutoRestart;
        private string _stopReason = "none";
        private int _inFlightCommands;
        private int _queuedCommands;

        public event Action<string>? OnRpcResponse;
        public event Action? OnWorkerExited;

        public WorkerProcess(Configuration config)
        {
            _config = config;
            _workerIdleTimeout = TimeSpan.FromMinutes(Math.Max(1, _config.Server?.WorkerIdleTimeoutMinutes ?? 5));
            _writerTask = Task.Run(ProcessQueueAsync);
        }

        private async Task RunHealthCheckAsync(CancellationToken ct)
        {
            await Task.Delay(5000, ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        if (ShouldStopForIdle())
                        {
                            Program.Log($"[Gateway] worker_idle_shutdown pid={_process.Id} idleTimeoutMinutes={_workerIdleTimeout.TotalMinutes}");
                            StopProcess("idle_timeout", suppressAutoRestart: true);
                            continue;
                        }

                        if (Volatile.Read(ref _inFlightCommands) <= 0)
                        {
                            await Task.Delay(15000, ct);
                            continue;
                        }

                        if ((DateTime.UtcNow - _lastResponse).TotalSeconds > 45)
                        {
                            Program.Log($"[Gateway] Warning: Worker unresponsive for 45s. Last activity: {_lastOperationInfo}. It may be processing a heavy load or a long KB operation.");
                        }
                        else
                        {
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
                            Interlocked.Decrement(ref _queuedCommands);
                            if (string.IsNullOrEmpty(jsonRpc))
                            {
                                continue;
                            }

                            if (!IsProcessRunning(_process))
                            {
                                Start();
                            }

                            string id = "unknown";
                            var countsAsActivity = false;
                            try
                            {
                                var json = JsonConvert.DeserializeObject<JObject>(jsonRpc);
                                if (json?["id"] != null)
                                {
                                    id = json["id"]?.ToString() ?? "unknown";
                                }

                                var method = json?["method"]?.ToString() ?? "unknown";
                                _lastOperationInfo = $"{method} (ID: {id})";
                                countsAsActivity = !string.Equals(id, "heartbeat", StringComparison.OrdinalIgnoreCase) &&
                                                   !string.Equals(method, "ping", StringComparison.OrdinalIgnoreCase);
                            }
                            catch
                            {
                            }

                            try
                            {
                                if (countsAsActivity)
                                {
                                    MarkActivity();
                                    Interlocked.Increment(ref _inFlightCommands);
                                }

                                await WaitForPipeReadyAsync(id, _cts.Token);

                                lock (_processLock)
                                {
                                    if (_pipeWriter != null)
                                    {
                                        _pipeWriter.WriteLine(jsonRpc);
                                        _pipeWriter.Flush();
                                        Program.Log($"[Gateway] Command written to pipe: {id}");
                                    }
                                    else
                                    {
                                        if (countsAsActivity)
                                        {
                                            CompleteInFlight();
                                        }

                                        Program.Log($"[Gateway] ERROR: Cannot send command {id}, pipe not available after wait.");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                if (countsAsActivity)
                                {
                                    CompleteInFlight();
                                }

                                Program.Log($"[Gateway] IPC Send Error ({id}): {ex.Message}");
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Program.Log($"[Gateway] Critical Error in ProcessQueueAsync: {ex.Message}");
                    try
                    {
                        await Task.Delay(1000, _cts.Token);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }

        private static bool IsProcessRunning(Process? process)
        {
            if (process == null)
            {
                return false;
            }

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
                        if (proc.Id == currentPid)
                        {
                            continue;
                        }

                        try
                        {
                            Program.Log($"[Gateway] Killing orphan {name} (PID {proc.Id})...");
                            proc.Kill(true);
                            proc.WaitForExit(3000);
                            Thread.Sleep(200);
                        }
                        catch
                        {
                        }
                    }
                }

                foreach (var proc in Process.GetProcessesByName("dotnet"))
                {
                    if (proc.Id == currentPid)
                    {
                        continue;
                    }

                    try
                    {
                        string cmdLine = GetCommandLine(proc);
                        if (string.IsNullOrEmpty(cmdLine))
                        {
                            continue;
                        }

                        bool isOurs =
                            cmdLine.Contains("GxMcp.Gateway.dll", StringComparison.OrdinalIgnoreCase) ||
                            cmdLine.Contains("GxMcp.Worker.dll", StringComparison.OrdinalIgnoreCase);

                        if (isOurs)
                        {
                            Program.Log($"[Gateway] Killing orphan dotnet-mcp (PID {proc.Id}, Cmd: {cmdLine})...");
                            proc.Kill(true);
                            proc.WaitForExit(3000);
                            Thread.Sleep(200);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static void KillOrphanWorkers()
        {
        }

        private static string GetCommandLine(Process process)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + process.Id);
                using var objects = searcher.Get();
                foreach (var obj in objects)
                {
                    return obj["CommandLine"]?.ToString() ?? string.Empty;
                }
            }
            catch
            {
            }

            return string.Empty;
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
                _suppressAutoRestart = false;
                _stopReason = "none";
                MarkActivity();
                _pipeReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string workerPath = _config.GeneXus?.WorkerExecutable ?? string.Empty;
                if (!Path.IsPathRooted(workerPath))
                {
                    workerPath = Path.Combine(baseDir, workerPath);
                }

                if (!File.Exists(workerPath))
                {
                    string[] devPaths = new[]
                    {
                        Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\..\src\GxMcp.Worker\bin\Debug\GxMcp.Worker.exe")),
                        Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\src\GxMcp.Worker\bin\Debug\GxMcp.Worker.exe")),
                        Path.Combine(baseDir, @"worker\GxMcp.Worker.exe")
                    };

                    foreach (var path in devPaths)
                    {
                        if (File.Exists(path))
                        {
                            workerPath = path;
                            break;
                        }
                    }
                }

                if (!File.Exists(workerPath))
                {
                    throw new FileNotFoundException($"Worker NOT FOUND at {workerPath}");
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = workerPath,
                    WorkingDirectory = Path.GetDirectoryName(workerPath) ?? string.Empty,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = System.Text.Encoding.UTF8,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                string kbPath = _config.Environment?.KBPath ?? string.Empty;
                startInfo.Arguments = $"--kb \"{kbPath}\"";
                startInfo.EnvironmentVariables["GX_PROGRAM_DIR"] = _config.GeneXus?.InstallationPath ?? string.Empty;
                startInfo.EnvironmentVariables["GX_KB_PATH"] = kbPath;
                startInfo.EnvironmentVariables["GX_SHADOW_PATH"] = _config.Environment?.GX_SHADOW_PATH ?? Path.Combine(kbPath, ".gx_mirror");
                startInfo.EnvironmentVariables["PATH"] = (_config.GeneXus?.InstallationPath ?? string.Empty) + ";" + Environment.GetEnvironmentVariable("PATH");

                if (_process != null)
                {
                    try
                    {
                        _process.Dispose();
                    }
                    catch
                    {
                    }
                }

                _process = new Process { StartInfo = startInfo };
                _process.EnableRaisingEvents = true;
                _process.Exited += (s, e) =>
                {
                    var exitedProcess = s as Process;
                    int exitCode = -1;
                    try
                    {
                        exitCode = exitedProcess?.ExitCode ?? -1;
                    }
                    catch
                    {
                    }

                    var restartAllowed = !_cts.Token.IsCancellationRequested && !_suppressAutoRestart;
                    Program.Log($"[Gateway] Worker process EXITED with code {exitCode}. reason={_stopReason} restartAllowed={restartAllowed}");
                    OnWorkerExited?.Invoke();
                    if (restartAllowed)
                    {
                        Task.Delay(2000, _cts.Token).ContinueWith(_ =>
                        {
                            if (!_cts.Token.IsCancellationRequested && (_process == null || _process.HasExited))
                            {
                                Program.Log("[Gateway] Auto-restarting Worker after crash...");
                                try
                                {
                                    Start();
                                }
                                catch (Exception ex)
                                {
                                    Program.Log($"[Gateway] Failed to auto-restart: {ex.Message}");
                                }
                            }
                        }, TaskContinuationOptions.OnlyOnRanToCompletion);
                    }
                };

                for (int attempt = 1; attempt <= 10; attempt++)
                {
                    try
                    {
                        _process.Start();
                        Program.Log($"[Gateway] worker_spawned pid={_process.Id} attempt={attempt} idleTimeoutMinutes={_workerIdleTimeout.TotalMinutes}");
                        break;
                    }
                    catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
                    {
                        Program.Log($"[Gateway] Access denied (5) when starting worker. Attempt {attempt}/10. File might be locked. Retrying in 1s...");
                        if (attempt == 10)
                        {
                            throw;
                        }

                        Thread.Sleep(1000);
                    }
                }

                _process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _lastResponse = DateTime.UtcNow;
                        if (e.Data.TrimStart().StartsWith("{") && e.Data.Contains("\"jsonrpc\""))
                        {
                            HandleWorkerRpcResponse(e.Data);
                            OnRpcResponse?.Invoke(e.Data);
                        }
                        else
                        {
                            Program.Log($"[Worker] {e.Data}");
                        }
                    }
                };

                _process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _lastResponse = DateTime.UtcNow;
                        Program.Log($"[Worker-Err] {e.Data}");
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
            Interlocked.Increment(ref _queuedCommands);
            await _commandChannel.Writer.WriteAsync(jsonRpc);
        }

        public void Stop()
        {
            _cts.Cancel();
            StopProcess("gateway_shutdown", suppressAutoRestart: true);
        }

        private void StopProcess(string reason, bool suppressAutoRestart)
        {
            lock (_processLock)
            {
                _stopReason = reason;
                _suppressAutoRestart = suppressAutoRestart;
                if (_pipeWriter != null)
                {
                    try { _pipeWriter.Dispose(); } catch { }
                    _pipeWriter = null;
                }

                if (_pipeReader != null)
                {
                    try { _pipeReader.Dispose(); } catch { }
                    _pipeReader = null;
                }

                if (_pipeServer != null)
                {
                    try { _pipeServer.Dispose(); } catch { }
                    _pipeServer = null;
                }

                _pipeReady.TrySetCanceled();
                Interlocked.Exchange(ref _queuedCommands, 0);
                Interlocked.Exchange(ref _inFlightCommands, 0);

                if (_process != null)
                {
                    try
                    {
                        if (!_process.HasExited)
                        {
                            _process.Kill(true);
                        }

                        _process.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Program.Log($"[Gateway] Error during process cleanup: {ex.Message}");
                    }

                    _process = null;
                }
            }
        }

        private void MarkActivity()
        {
            _lastActivityUtc = DateTime.UtcNow;
        }

        private bool ShouldStopForIdle()
        {
            if (_workerIdleTimeout <= TimeSpan.Zero)
            {
                return false;
            }

            if (Volatile.Read(ref _queuedCommands) > 0 || Volatile.Read(ref _inFlightCommands) > 0)
            {
                return false;
            }

            if (_isStarting)
            {
                return false;
            }

            return DateTime.UtcNow - _lastActivityUtc >= _workerIdleTimeout;
        }

        private void HandleWorkerRpcResponse(string json)
        {
            try
            {
                var payload = JObject.Parse(json);
                var id = payload["id"]?.ToString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    return;
                }

                if (!string.Equals(id, "heartbeat", StringComparison.OrdinalIgnoreCase))
                {
                    MarkActivity();
                    CompleteInFlight();
                }
            }
            catch
            {
            }
        }

        private void CompleteInFlight()
        {
            while (true)
            {
                var current = Volatile.Read(ref _inFlightCommands);
                if (current <= 0)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _inFlightCommands, current - 1, current) == current)
                {
                    return;
                }
            }
        }
    }
}
