using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;

namespace GxMcp.Gateway
{
    class Program
    {
        private const string McpAxiSchemaVersion = "mcp-axi/1";
        private static WorkerProcess? _worker;
        private sealed class PendingWorkerRequest
        {
            public TaskCompletionSource<string> CompletionSource { get; init; } = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            public string ToolName { get; init; } = "unknown";
            public string CorrelationId { get; init; } = string.Empty;
            public string? OperationId { get; init; }
            public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
        }

        private static ConcurrentDictionary<string, PendingWorkerRequest> _pendingRequests = new ConcurrentDictionary<string, PendingWorkerRequest>();
        private static ConcurrentDictionary<string, JObject> _semanticCache = new ConcurrentDictionary<string, JObject>();
        private static HttpSessionRegistry _httpSessions = new HttpSessionRegistry(TimeSpan.FromMinutes(10));
        private static readonly OperationTracker _operationTracker = new OperationTracker(TimeSpan.FromMinutes(60));
        private static int _workerWarmupStarted;
        private static readonly TimeSpan _pendingRequestRetention = TimeSpan.FromMinutes(65);
        private static readonly string _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gateway_debug.log");
        private static readonly BlockingCollection<string> _logQueue = new BlockingCollection<string>();
        private static readonly string[] _defaultLocalOrigins = new[]
        {
            "http://localhost",
            "http://127.0.0.1",
            "https://localhost",
            "https://127.0.0.1"
        };

        private static readonly object _logLock = new object();
        private static Configuration? _activeConfig;

        public static void TryWriteStderr(string message)
        {
            Log(message);
        }

        public static async Task TryWriteStdout(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;
            try { 
                await Console.Out.WriteLineAsync(msg);
                await Console.Out.FlushAsync();
            } catch { }
        }

        private static void InitializeLogging()
        {
            /* Background writer disabled for stability */
            Log("=== Gateway starting (Stdio Mode) ===");
        }

        public static void Log(string msg)
        {
            try {
                lock (_logLock) {
                    File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\n");
                }
            } catch { }
        }

        private static async Task RunSessionCleanupLoop(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    if (_activeConfig != null)
                    {
                        GatewayProcessLease.RefreshCurrentProcess(_activeConfig);
                    }

                    int removed = _httpSessions.CleanupExpired();
                    if (removed > 0)
                    {
                        Log($"[HTTP] Removed {removed} expired MCP session(s).");
                    }

                    _operationTracker.CleanupExpired();
                    int stalePending = CleanupStalePendingRequests();
                    if (stalePending > 0)
                    {
                        Log($"[Gateway] Removed {stalePending} stale pending worker request(s).");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static int CleanupStalePendingRequests()
        {
            int removed = 0;
            DateTime cutoff = DateTime.UtcNow - _pendingRequestRetention;
            foreach (var kvp in _pendingRequests.ToArray())
            {
                if (kvp.Value.CreatedAtUtc > cutoff)
                {
                    continue;
                }

                if (_pendingRequests.TryRemove(kvp.Key, out var pending))
                {
                    _operationTracker.MarkFailedByRequest(kvp.Key, "Pending worker request expired before completion.");
                    pending.CompletionSource.TrySetResult(JsonConvert.SerializeObject(new
                    {
                        jsonrpc = "2.0",
                        id = kvp.Key,
                        error = new
                        {
                            code = -32603,
                            message = "Pending worker request expired before completion."
                        }
                    }));
                    removed++;
                }
            }

            return removed;
        }

        private static void BroadcastNotification(string method, object payload)
        {
            _ = Task.Run(() => {
                try {
                    string json = JsonConvert.SerializeObject(new
                    {
                        jsonrpc = "2.0",
                        method,
                        @params = payload
                    });

                    foreach (var session in _httpSessions.ActiveSessions)
                    {
                        QueueSessionMessage(session, json);
                    }
                } catch (Exception ex) {
                    Log($"[Broadcast] Error: {ex.Message}");
                }
            });
        }

        private static void BroadcastToolsListChanged(string reason)
        {
            BroadcastNotification("notifications/tools/list_changed", new
            {
                reason,
                timestamp = DateTime.UtcNow
            });
        }

        private static void BroadcastResourcesListChanged(string reason)
        {
            BroadcastNotification("notifications/resources/list_changed", new
            {
                reason,
                timestamp = DateTime.UtcNow
            });
        }

        private static void BroadcastResourceUpdated(string uri, string reason)
        {
            BroadcastNotification("notifications/resources/updated", new
            {
                uri,
                reason,
                timestamp = DateTime.UtcNow
            });
        }

        public static async Task Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += async (s, e) => {
                string msg = $"[{DateTime.Now}] FATAL UNHANDLED: {e.ExceptionObject}\n";
                var errorObj = new { jsonrpc = "2.0", method = "notifications/message", @params = new { level = "error", logger = "gateway", data = msg } };
                await TryWriteStdout(Newtonsoft.Json.JsonConvert.SerializeObject(errorObj));
                try { File.AppendAllText("gateway_panic.log", msg); } catch { }
            };

            // Register encoding provider for Windows-1252 support in .NET 8
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            try
            {
                Console.InputEncoding = System.Text.Encoding.UTF8;
                Console.OutputEncoding = System.Text.Encoding.UTF8;
            }
            catch (IOException)
            {
                // Detached HTTP-only launches may not have a console handle.
            }

            InitializeLogging();
            var config = Configuration.Load();
            _activeConfig = config;
            Log("[Gateway] Startup orphan-kill disabled. Existing gateway reuse is handled by the extension client.");
            AppDomain.CurrentDomain.ProcessExit += (_, __) =>
            {
                if (_activeConfig != null)
                {
                    GatewayProcessLease.ReleaseCurrentProcess(_activeConfig);
                }
            };

            var leaseRegistration = GatewayProcessLease.TryRegisterCurrentProcess(config);
            bool isMaster = leaseRegistration.Success;

            if (!isMaster)
            {
                if (leaseRegistration.IsDuplicate && leaseRegistration.Lease != null)
                {
                    Log($"[Gateway] existing_master_detected currentPid={Environment.ProcessId} masterPid={leaseRegistration.Lease.ProcessId}");
                    
                    if (leaseRegistration.Lease.HttpPort > 0) 
                    {
                        bool shouldPromote = await RunMcpProxyAsync(leaseRegistration.Lease, config);
                        if (!shouldPromote) return;
                        
                        Log("[Gateway] Starting promotion to Master...");
                        var forced = GatewayProcessLease.ForceRegisterCurrentProcess(config);
                        if (!forced.Success) {
                            Log("[Gateway] Promotion failed: lease acquisition blocked.");
                            return;
                        }
                        isMaster = true;
                    }
                    else 
                    {
                        Log($"[Gateway] Existing master (PID {leaseRegistration.Lease.ProcessId}) has no HTTP port. Reusing or exiting.");
                        return;
                    }
                }
                else 
                {
                    Log($"[Gateway] Registration failed: {leaseRegistration.FailureReason}");
                    return;
                }
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) => {
                Log("FATAL UNHANDLED EXCEPTION: " + (e.ExceptionObject as Exception)?.ToString());
            };

            TaskScheduler.UnobservedTaskException += (s, e) => {
                Log("UNOBSERVED TASK EXCEPTION: " + e.Exception?.ToString());
                e.SetObserved();
            };

            Log("=== Gateway starting (Stdio Mode) ===");
            
            _httpSessions = new HttpSessionRegistry(TimeSpan.FromMinutes(config.Server?.SessionIdleTimeoutMinutes ?? 10));
            
            // Subscribing to Configuration Changes
            Configuration.OnConfigurationChanged += (newConfig) => {
                if (newConfig.Environment?.KBPath != config.Environment?.KBPath || 
                    newConfig.GeneXus?.InstallationPath != config.GeneXus?.InstallationPath ||
                    newConfig.Environment?.GX_SHADOW_PATH != config.Environment?.GX_SHADOW_PATH ||
                    newConfig.Server?.HttpPort != config.Server?.HttpPort ||
                    newConfig.Server?.WorkerIdleTimeoutMinutes != config.Server?.WorkerIdleTimeoutMinutes) {
                    Log($"[Gateway] Core configuration changed! Restarting Worker process...");
                    config = newConfig; // Update reference
                    _activeConfig = config;
                    GatewayProcessLease.RefreshCurrentProcess(config);
                    RestartWorker(config);
                    BroadcastResourcesListChanged("core_configuration_changed");
                } else {
                    Log($"[Gateway] Minor configuration changed. Ignoring.");
                }
            };

            // 1. Start HTTP Server first (it's critical for VS Code communication)
            if (config.Server?.HttpPort > 0)
            {
                Log($"[Gateway] Starting HTTP server on port {config.Server.HttpPort}...");
                _ = Task.Run(async () => {
                    int retryCount = 0;
                    while (retryCount < 5) {
                        try { 
                            await StartHttpServer(config); 
                            Log("[Gateway] HTTP server bound and active.");
                            while(true) {
                                await Task.Delay(30000);
                                Log("[Gateway] Heartbeat: HTTP server still active.");
                            }
                        }
                        catch (Exception exHttp) { 
                            Log($"[HTTP] Bind failure (5000): {exHttp.Message}. Attempting port recovery ({retryCount + 1}/5)...");
                            TryKillProcessOnPort(config.Server?.HttpPort ?? 5000);
                            retryCount++;
                            await Task.Delay(1000);
                        }
                    }
                });
            }

            // 2. Start Worker in background
            Log("[Gateway] Initializing Worker lifecycle...");
            StartWorker(config);
            Log("[Gateway] Worker lifecycle ready.");

            // 3. Subscribing to KB changes for Semantic Cache Invalidation
            if (!string.IsNullOrEmpty(config.Environment?.KBPath))
            {
                Log("[Gateway] Setting up .gx_mirror watcher...");
                try 
                {
                    string mirrorPath = Path.Combine(config.Environment.KBPath, ".gx_mirror");
                    if (!Directory.Exists(mirrorPath)) Directory.CreateDirectory(mirrorPath);
                    var watcher = new FileSystemWatcher(mirrorPath) 
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                        EnableRaisingEvents = true
                    };
                    watcher.Changed += (s, e) => {
                        Log($"[Cache] Invalidation triggered by external change: {e.Name}");
                        _semanticCache.Clear();
                        BroadcastResourceUpdated("genexus://objects", "external_kb_change");
                    };
                    Log("[Gateway] .gx_mirror watcher active.");
                } catch (Exception ex) { Log($"[Cache] Watcher error: {ex.Message}"); }
            }

            if (config.Server?.McpStdio == true)
            {
                Log("[Gateway] Entering Stdio Loop...");
                var reader = Console.In;
                while (true)
                {
                    string? line = null;
                    try { line = await reader.ReadLineAsync(); } catch { }

                    if (line == null)
                    {
                        if (config.Server?.HttpPort > 0)
                        {
                            Log("Stdio closed, keeping alive for HTTP...");
                            await Task.Delay(-1);
                        }
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (!line.Trim().StartsWith("{")) {
                        Log($"[Protocol] Ignored non-JSON noise on stdin: {line}");
                        continue;
                    }

                    try
                    {
                        var request = JObject.Parse(line);
                        string requestId = request["id"]?.ToString() ?? "unknown";
                        var response = await ProcessMcpRequest(request);
                        if (response != null)
                        {
                            await TryWriteStdout(response.ToString(Formatting.None));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("MCP Error: " + ex.Message);
                    }
                }
            }
            else if (config.Server?.HttpPort > 0)
            {
                Log("[Gateway] MCP stdio disabled. Serving HTTP only.");
                await Task.Delay(-1);
            }
        }

        private static async Task<bool> RunMcpProxyAsync(GatewayLeaseRecord master, Configuration config)
        {
            string baseUrl = $"http://localhost:{master.HttpPort}/mcp";
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30); // Do not let proxy hang forever if master is dead
            httpClient.DefaultRequestHeaders.Add("MCP-Protocol-Version", McpRouter.SupportedProtocolVersion);
            
            string? sessionId = null;
            var reader = Console.In;
            var cts = new CancellationTokenSource();
            var ct = cts.Token;

            Log($"[Proxy] Proxy mode active (Master PID {master.ProcessId} on port {master.HttpPort}).");

            while (true)
            {
                string? line = null;
                try { line = await reader.ReadLineAsync(ct); } catch { break; }
                if (line == null) break;

                int retryCount = 0;
                bool success = false;
                while (retryCount < 3 && !success)
                {
                    try
                    {
                        string body = line;
                        var request = JObject.Parse(body);
                        string requestId = request["id"]?.ToString() ?? "unknown";
                        var content = new StringContent(body, Encoding.UTF8, "application/json");
                        
                        if (sessionId != null) content.Headers.Add("MCP-Session-Id", sessionId);

                        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, baseUrl) { Content = content };
                        if (sessionId != null) requestMessage.Headers.Add("MCP-Session-Id", sessionId);

                        var response = await httpClient.SendAsync(requestMessage, ct);
                        
                        if (sessionId == null && response.Headers.TryGetValues("MCP-Session-Id", out var values))
                        {
                            sessionId = values.FirstOrDefault();
                            if (sessionId != null)
                            {
                                Log($"[Proxy] Handshake complete. ID: {sessionId}");
                                // Wait a moment for master to stabilize before streaming notifications
                                await Task.Delay(2000);
                                _ = Task.Run(() => RunProxySseForwarderAsync(master.HttpPort, sessionId, cts.Token));
                            }
                        }

                        if (response.IsSuccessStatusCode)
                        {
                            string responseBody = await response.Content.ReadAsStringAsync(ct);
                            if (string.IsNullOrWhiteSpace(responseBody))
                            {
                                Log($"[Proxy] Master returned empty response for command {requestId}. Retrying...");
                                throw new Exception("Empty response from master");
                            }

                            if (!responseBody.Trim().StartsWith("{"))
                            {
                                Log($"[Proxy] Master returned non-JSON response: {responseBody}");
                                throw new Exception("Invalid response from master");
                            }

                            await TryWriteStdout(responseBody);
                            success = true;
                        }
                        else
                        {
                            string remoteError = await response.Content.ReadAsStringAsync(ct);
                            Log($"[Proxy] Master status {response.StatusCode}: {remoteError}");
                            var id = request["id"];
                            if (id != null)
                            {
                                var errorResponse = new JObject
                                {
                                    ["jsonrpc"] = "2.0",
                                    ["id"] = id.DeepClone(),
                                    ["error"] = new JObject { ["code"] = (int)response.StatusCode, ["message"] = $"Master error: {response.StatusCode}" }
                                };
                                await TryWriteStdout(errorResponse.ToString(Formatting.None));
                            }
                            success = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        retryCount++;
                        Log($"[Proxy] Connection failed to Master ({retryCount}/3): {ex.Message}");
                        if (retryCount >= 3)
                        {
                            Log("[Proxy] Master unresponsive. Triggering promotion...");
                            // We return true but we need to keep the last line for the Master to process!
                            // To keep it simple, we let Main know to promote. 
                            // The IDE will likely retry the last command or we can buffer it.
                            return true; 
                        }
                        await Task.Delay(1000);
                    }
                }
            }
            cts.Cancel();
            Log("[Proxy] Stdio closed.");
            return false;
        }

        private static async Task RunProxySseForwarderAsync(int port, string sessionId, CancellationToken ct)
        {
            string url = $"http://localhost:{port}/mcp";
            using var client = new HttpClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.Add("MCP-Session-Id", sessionId);
            client.DefaultRequestHeaders.Add("MCP-Protocol-Version", McpRouter.SupportedProtocolVersion);

            try
            {
                Log("[Proxy-SSE] Notification link active.");
                using var stream = await client.GetStreamAsync(url, ct);
                using var reader = new StreamReader(stream);
                while (!ct.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(ct);
                    if (line == null) break;

                    if (line.StartsWith("data: "))
                    {
                        string data = line.Substring(6).Trim();
                        // SSE message events (notifications) usually come in formatted as data blocks
                        // We must enforce jsonrpc wrapper to avoid client parsing errors on metadata like {"sessionId":"..."}
                        if (data.StartsWith("{") && data.EndsWith("}") && data.Contains("\"jsonrpc\""))
                        {
                            await TryWriteStdout(data);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"[Proxy-SSE] Background channel error: {ex.Message}");
            }
        }

        private static void StartWorker(Configuration config)
        {
            _worker = new WorkerProcess(config);
            _worker.OnRpcResponse += HandleWorkerResponse;
            _worker.OnWorkerExited += () => {
                Log("Worker Process Exited. Notifying all pending requests...");
                foreach (var kvp in _pendingRequests.ToArray())
                {
                    string id = kvp.Key;
                    if (_pendingRequests.TryRemove(id, out var pending))
                    {
                        _operationTracker.MarkFailedByRequest(id, "GeneXus MCP Worker crashed/exited.");
                        var errorJson = JsonConvert.SerializeObject(new { 
                            jsonrpc = "2.0", 
                            id = id, 
                            error = new { code = -32603, message = "GeneXus MCP Worker crashed/exited." } 
                        });
                        pending.CompletionSource.TrySetResult(errorJson);
                    }
                }
            };
        }

        private static void RestartWorker(Configuration config)
        {
            if (_worker != null)
            {
                try { _worker.Stop(); } catch { }
            }
            // Clear cache on KB change
            _semanticCache.Clear();
            StartWorker(config);
            BroadcastToolsListChanged("worker_restarted");
            BroadcastResourcesListChanged("worker_restarted");
        }

        private static void HandleWorkerResponse(string json)
        {
            try {
                var val = JObject.Parse(json);
                string? id = val["id"]?.ToString();
                
                if (string.IsNullOrEmpty(id))
                {
                    // JSON-RPC Notification from Worker
                    string? method = val["method"]?.ToString();
                    if (method == "notifications/resources/updated")
                    {
                        var p = val["params"];
                        string name = p?["name"]?.ToString() ?? "unknown";
                        Log($"[Gateway] Notification from Worker: Resource {name} updated externally.");
                        BroadcastResourceUpdated($"genexus://objects/{name}", "external_kb_change");
                    }
                    return;
                }

                _operationTracker.CompleteFromWorker(id, val);
                if (_pendingRequests.TryRemove(id, out var pending))
                {
                    pending.CompletionSource.TrySetResult(json);
                    if (!string.IsNullOrWhiteSpace(pending.OperationId))
                    {
                        BroadcastNotification("notifications/message", new
                        {
                            level = "info",
                            logger = "operation",
                            data = $"Operation {pending.OperationId} finished.",
                            operationId = pending.OperationId,
                            correlationId = pending.CorrelationId,
                            status = val["error"] != null ? "Failed" : "Completed",
                            timestamp = DateTime.UtcNow
                        });
                    }
                }
            } catch (Exception ex) { Log($"HandleWorkerResponse Error: {ex.Message}"); }
        }

        private static JObject BuildWorkerRpcRequest(JObject workerCommand, string requestId)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestId,
                ["method"] = workerCommand["module"]?.ToString() ?? string.Empty,
                ["action"] = workerCommand["action"]?.DeepClone(),
                ["target"] = workerCommand["target"]?.DeepClone(),
                ["payload"] = workerCommand["payload"]?.DeepClone(),
                ["params"] = workerCommand.DeepClone()
            };
        }

        private static async Task<JObject?> SendWorkerCommandAsync(
            JObject workerCommand,
            int timeoutMs,
            string timeoutLogMessage,
            Func<JObject, JObject> onSuccess,
            Func<string?, string, JObject> onTimeout,
            string toolName = "unknown",
            JObject? toolArgs = null,
            bool trackOperation = false)
        {
            string requestId = Guid.NewGuid().ToString();
            string correlationId = Guid.NewGuid().ToString("N");
            string? operationId = null;

            if (trackOperation)
            {
                operationId = _operationTracker.StartOperation(requestId, toolName, toolArgs, correlationId);
                BroadcastNotification("notifications/message", new
                {
                    level = "info",
                    logger = "operation",
                    data = $"Operation {operationId} started for tool {toolName}.",
                    operationId,
                    correlationId,
                    status = "Running",
                    timestamp = DateTime.UtcNow
                });
            }

            workerCommand["correlationId"] = correlationId;
            var pending = new PendingWorkerRequest
            {
                CompletionSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously),
                ToolName = toolName,
                CorrelationId = correlationId,
                OperationId = operationId,
                CreatedAtUtc = DateTime.UtcNow
            };
            _pendingRequests[requestId] = pending;

            var workerRequest = BuildWorkerRpcRequest(workerCommand, requestId);
            await _worker!.SendCommandAsync(workerRequest.ToString(Formatting.None));

            var completedTask = await Task.WhenAny(pending.CompletionSource.Task, Task.Delay(timeoutMs));
            if (completedTask == pending.CompletionSource.Task)
            {
                var workerResponse = JObject.Parse(await pending.CompletionSource.Task);
                if (workerResponse["result"] is JObject workerResultObj && workerResultObj["correlationId"] == null)
                {
                    workerResultObj["correlationId"] = correlationId;
                }
                if (workerResponse["error"] is JObject workerErrorObj && workerErrorObj["correlationId"] == null)
                {
                    workerErrorObj["correlationId"] = correlationId;
                }
                return onSuccess(workerResponse);
            }

            if (!string.IsNullOrWhiteSpace(operationId))
            {
                _operationTracker.MarkTimeout(operationId);
                BroadcastNotification("notifications/message", new
                {
                    level = "warning",
                    logger = "operation",
                    data = $"Operation {operationId} is still running after timeout budget.",
                    operationId,
                    correlationId,
                    status = "Running",
                    timestamp = DateTime.UtcNow
                });
            }
            else
            {
                _pendingRequests.TryRemove(requestId, out _);
            }

            Log($"{timeoutLogMessage} (operationId={operationId ?? "n/a"}, correlationId={correlationId})");
            return onTimeout(operationId, correlationId);
        }

        internal static int GetToolTimeoutMs(string? toolName, JObject? args)
        {
            if (toolName == "genexus_lifecycle" || toolName == "genexus_analyze" || toolName == "genexus_test")
            {
                return 600000;
            }

            string? part = args?["part"]?.ToString();
            if (string.Equals(toolName, "genexus_edit", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(part, "Layout", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "WebForm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "Source", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "Events", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "PatternInstance", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "PatternVirtual", StringComparison.OrdinalIgnoreCase))
                {
                    return 180000;
                }
            }

            if (string.Equals(toolName, "genexus_import_object", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(part, "Source", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "Events", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "Rules", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "Variables", StringComparison.OrdinalIgnoreCase))
                {
                    return 300000;
                }
            }

            return 60000;
        }

        private static async Task<JObject?> ProcessMcpRequest(JObject request)
        {
            string? method = request["method"]?.ToString();
            var idToken = request["id"];

            // Protocol level
            var mcpResponse = McpRouter.Handle(request);
            if (mcpResponse != null)
            {
                if (string.Equals(method, "initialize", StringComparison.OrdinalIgnoreCase))
                {
                    TriggerWorkerWarmupOnce();
                }
                return new JObject { ["jsonrpc"] = "2.0", ["id"] = idToken?.DeepClone(), ["result"] = JToken.FromObject(mcpResponse) };
            }

            // Tool Calls
            if (method == "tools/call")
            {
                // ... (logic handled below) ...
            }

            // Resource Calls
            if (method == "resources/read")
            {
                var rawWorkerCmd = McpRouter.ConvertResourceCall(request);
                var workerCmd = rawWorkerCmd != null ? JObject.FromObject(rawWorkerCmd) : null;
                if (workerCmd != null)
                {
                    workerCmd["client"] = "mcp";
                    return await SendWorkerCommandAsync(
                        workerCmd,
                        60000,
                        $"Timeout waiting for resource: {request["params"]?["uri"]}",
                        resultObj =>
                        {
                            if (resultObj["error"] != null || resultObj["status"]?.ToString() == "Error")
                            {
                                string errorMsg = resultObj["error"]?.ToString() ?? resultObj["details"]?.ToString() ?? "Unknown error reading resource";
                                
                                // Enrich with suggestions if available (from HealingService)
                                string? suggestion = resultObj["suggestion"]?.ToString();
                                if (!string.IsNullOrEmpty(suggestion)) errorMsg += "\n" + suggestion;
                                
                                string? tip = resultObj["actionable_tip"]?.ToString();
                                if (!string.IsNullOrEmpty(tip)) errorMsg += "\nTip: " + tip;

                                return new JObject
                                {
                                    ["jsonrpc"] = "2.0",
                                    ["id"] = idToken?.DeepClone(),
                                    ["error"] = JToken.FromObject(new { code = -32603, message = $"GeneXus MCP Worker error: {errorMsg}" })
                                };
                            }

                            var content = resultObj["result"]?.ToString() ?? resultObj["source"]?.ToString() ?? "";
                            return new JObject
                            {
                                ["jsonrpc"] = "2.0",
                                ["id"] = idToken?.DeepClone(),
                                ["result"] = JToken.FromObject(new
                                {
                                    contents = new[]
                                    {
                                        new
                                        {
                                            uri = request["params"]?["uri"]?.ToString(),
                                            mimeType = "text/plain",
                                            text = content
                                        }
                                    }
                                })
                            };
                        },
                        (_, correlationId) => new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idToken?.DeepClone(),
                            ["error"] = JToken.FromObject(new { code = -32000, message = "GeneXus MCP Worker timed out reading resource.", correlationId })
                        },
                        toolName: "resources/read",
                        toolArgs: request["params"] as JObject,
                        trackOperation: false);
                }
            }

            // Tool Calls (Actual logic)
            if (method == "tools/call")
            {
                var paramsObj = request["params"] as JObject;
                string toolName = paramsObj?["name"]?.ToString() ?? "";
                var args = paramsObj?["arguments"] as JObject;

                if (string.Equals(toolName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase))
                {
                    string? lifecycleAction = args?["action"]?.ToString();
                    string? lifecycleTarget = args?["target"]?.ToString();
                    if ((string.Equals(lifecycleAction, "status", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(lifecycleAction, "result", StringComparison.OrdinalIgnoreCase)) &&
                        !string.IsNullOrWhiteSpace(lifecycleTarget) &&
                        lifecycleTarget.StartsWith("op:", StringComparison.OrdinalIgnoreCase))
                    {
                        string operationId = lifecycleTarget.Substring(3);
                        JObject opPayload = string.Equals(lifecycleAction, "result", StringComparison.OrdinalIgnoreCase)
                            ? _operationTracker.BuildOperationResult(operationId)
                            : _operationTracker.BuildOperationStatus(operationId);
                        return BuildToolTextResponse(
                            idToken,
                            opPayload,
                            isError: string.Equals(opPayload["status"]?.ToString(), "NotFound", StringComparison.OrdinalIgnoreCase),
                            toolName: "genexus_lifecycle",
                            toolArgs: args);
                    }

                    if (string.Equals(lifecycleAction, "status", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(lifecycleTarget, "gateway:metrics", StringComparison.OrdinalIgnoreCase))
                    {
                        return BuildToolTextResponse(idToken, _operationTracker.BuildMetricsPayload(), isError: false, toolName: "genexus_lifecycle", toolArgs: args);
                    }
                }
                
                // 1. CACHE INVALIDATION: If it's a write operation or a re-index, clear the cache
                if (IsMutatingTool(toolName, args))
                {
                    Log($"[Cache] Invalidation triggered by {toolName}");
                    _semanticCache.Clear();
                    BroadcastResourcesListChanged($"cache_invalidated:{toolName}");
                    BroadcastResourceUpdated("genexus://objects", $"tool:{toolName}");
                }

                // 2. SEMANTIC CACHE: Try to get from cache for read-only tools
                string cacheKey = $"{toolName}:{args?.ToString(Formatting.None)}";
                if (_semanticCache.TryGetValue(cacheKey, out var cachedResponse))
                {
                    Log($"[Cache] HIT for {toolName}");
                    var cloned = cachedResponse.DeepClone() as JObject;
                    if (cloned != null) {
                        cloned["id"] = idToken?.DeepClone();
                        return cloned;
                    }
                }

                object? rawWorkerCmd = null;
                if (string.Equals(toolName, "genexus_open_kb", StringComparison.OrdinalIgnoreCase))
                {
                    rawWorkerCmd = new
                    {
                        module = "KB",
                        action = "Open",
                        target = args?["path"]?.ToString()
                    };
                }
                else if (string.Equals(toolName, "genexus_export_object", StringComparison.OrdinalIgnoreCase))
                {
                    rawWorkerCmd = new
                    {
                        module = "Object",
                        action = "ExportText",
                        target = args?["name"]?.ToString(),
                        path = args?["outputPath"]?.ToString(),
                        part = args?["part"]?.ToString() ?? "Source",
                        type = args?["type"]?.ToString(),
                        overwrite = args?["overwrite"]?.ToObject<bool?>() ?? false
                    };
                }
                else if (string.Equals(toolName, "genexus_import_object", StringComparison.OrdinalIgnoreCase))
                {
                    rawWorkerCmd = new
                    {
                        module = "Object",
                        action = "ImportText",
                        target = args?["name"]?.ToString(),
                        path = args?["inputPath"]?.ToString(),
                        part = args?["part"]?.ToString() ?? "Source",
                        type = args?["type"]?.ToString()
                    };
                }

                rawWorkerCmd ??= McpRouter.ConvertToolCall(request);
                var workerCmd = rawWorkerCmd != null ? JObject.FromObject(rawWorkerCmd) : null;
                if (workerCmd != null)
                {
                    workerCmd["client"] = "mcp";
                    int timeoutMs = GetToolTimeoutMs(toolName, args);

                    return await SendWorkerCommandAsync(
                        workerCmd,
                        timeoutMs,
                        $"Timeout waiting for tool: {toolName}",
                        resultObj =>
                        {
                            JToken? finalResult = null;
                            try {
                                finalResult = TruncateResponseIfNeeded(resultObj["result"] ?? resultObj["error"], toolName);
                            } catch (Exception exTrunc) {
                                Log($"[Gateway] Error during truncation: {exTrunc.Message}");
                                finalResult = resultObj["result"] ?? resultObj["error"];
                            }

                            bool isError = resultObj["error"] != null || string.Equals(resultObj["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase);
                            JToken axiPayload = NormalizeToolPayloadForAxi(finalResult, toolName, args, isError);

                            var response = new JObject
                            {
                                ["jsonrpc"] = "2.0",
                                ["id"] = idToken?.DeepClone(),
                                ["result"] = JToken.FromObject(new
                                {
                                    content = new[] { new { type = "text", text = axiPayload.ToString(Formatting.None) } },
                                    isError
                                })
                            };

                            if (!isError && !toolName.Contains("write") && !toolName.Contains("patch"))
                            {
                                _semanticCache[cacheKey] = response;
                            }

                            return response;
                        },
                        (operationId, correlationId) =>
                        {
                            string message = $"GeneXus MCP Worker timed out executing tool: {toolName}.";
                            var timeoutPayload = new JObject
                            {
                                ["status"] = "Running",
                                ["error"] = "Gateway timeout waiting for worker response.",
                                ["message"] = message,
                                ["correlationId"] = correlationId,
                                ["retriable"] = true
                            };

                            var help = new JArray();
                            if (!string.IsNullOrWhiteSpace(operationId))
                            {
                                timeoutPayload["operationId"] = operationId;
                                help.Add($"Operation is still running. Query genexus_lifecycle(action='status', target='op:{operationId}') or action='result'.");
                            }
                            else
                            {
                                help.Add("Retry with narrower scope or lower limit.");
                            }
                            timeoutPayload["help"] = help;

                            JToken axiPayload = NormalizeToolPayloadForAxi(timeoutPayload, toolName, args, true);
                            return new JObject
                            {
                                ["jsonrpc"] = "2.0",
                                ["id"] = idToken?.DeepClone(),
                                ["result"] = JToken.FromObject(new
                                {
                                    content = new[] { new { type = "text", text = axiPayload.ToString(Formatting.None) } },
                                    isError = true
                                })
                            };
                        },
                        toolName: toolName,
                        toolArgs: args,
                        trackOperation: true);
                }
            }

            // Explicitly return an error for unknown tools if convert failed
            if (method == "tools/call")
            {
                return new JObject { 
                    ["jsonrpc"] = "2.0", 
                    ["id"] = idToken?.DeepClone(), 
                    ["error"] = JToken.FromObject(new { code = -32601, message = "Method not found or could not be converted." }) 
                };
            }
            
            return null;
        }

        private static void TriggerWorkerWarmupOnce()
        {
            if (Interlocked.CompareExchange(ref _workerWarmupStarted, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_worker == null)
                    {
                        Log("[Warmup] Worker not available, skipping warmup.");
                        return;
                    }

                    Log("[Warmup] Starting worker warmup sequence...");
                    BroadcastNotification("notifications/message", new
                    {
                        level = "info",
                        logger = "warmup",
                        data = "Worker warmup started.",
                        timestamp = DateTime.UtcNow
                    });

                    var listCommand = new JObject
                    {
                        ["module"] = "List",
                        ["action"] = "Objects",
                        ["target"] = string.Empty,
                        ["limit"] = 1,
                        ["offset"] = 0,
                        ["client"] = "mcp"
                    };

                    var listResponse = await SendWorkerCommandAsync(
                        listCommand,
                        30000,
                        "Warmup list timeout",
                        workerResponse => workerResponse,
                        (_, correlationId) => new JObject
                        {
                            ["error"] = new JObject
                            {
                                ["message"] = "Warmup list operation timed out.",
                                ["correlationId"] = correlationId
                            }
                        },
                        toolName: "gateway_warmup_list",
                        trackOperation: false);

                    string? objectName = listResponse?["result"]?["results"]?.First?["name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(objectName))
                    {
                        var readCommand = new JObject
                        {
                            ["module"] = "Read",
                            ["action"] = "ExtractSource",
                            ["target"] = objectName,
                            ["part"] = "Source",
                            ["offset"] = 0,
                            ["limit"] = 1,
                            ["client"] = "mcp"
                        };

                        await SendWorkerCommandAsync(
                            readCommand,
                            30000,
                            "Warmup read timeout",
                            workerResponse => workerResponse,
                            (_, correlationId) => new JObject
                            {
                                ["error"] = new JObject
                                {
                                    ["message"] = "Warmup read operation timed out.",
                                    ["correlationId"] = correlationId
                                }
                            },
                            toolName: "gateway_warmup_read",
                            trackOperation: false);
                    }

                    Log("[Warmup] Worker warmup finished.");
                    BroadcastNotification("notifications/message", new
                    {
                        level = "info",
                        logger = "warmup",
                        data = "Worker warmup finished.",
                        timestamp = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    Log("[Warmup] Worker warmup failed: " + ex.Message);
                    BroadcastNotification("notifications/message", new
                    {
                        level = "warning",
                        logger = "warmup",
                        data = "Worker warmup failed: " + ex.Message,
                        timestamp = DateTime.UtcNow
                    });
                }
            });
        }

        private static JToken TruncateResponseIfNeeded(JToken? result, string toolName)
        {
            if (result == null) return JValue.CreateNull();
            
            string? readPart = (result as JObject)?["part"]?.ToString();
            bool isXmlMetadataRead = string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase) &&
                                     (string.Equals(readPart, "Layout", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(readPart, "WebForm", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(readPart, "PatternInstance", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(readPart, "PatternVirtual", StringComparison.OrdinalIgnoreCase));

            string raw = result.ToString(Formatting.None);
            int softBudget = isXmlMetadataRead
                ? 220000
                : string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase)
                ? 24000
                : string.Equals(toolName, "genexus_asset", StringComparison.OrdinalIgnoreCase)
                    ? 400000
                    : 60000;
            if (raw.Length < softBudget) return result;

            Log($"[Budget] Truncating response for {toolName} ({raw.Length} chars)");

            if (result is JObject obj)
            {
                if (string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var metadataField in new[] { "variables", "calls", "dataSchema", "patternMetadata" })
                    {
                        if (obj[metadataField] != null)
                        {
                            obj.Remove(metadataField);
                            obj["isTruncated"] = true;
                            obj["message"] = "Gateway trimmed derived metadata from genexus_read to keep the response within the MCP context budget.";
                        }
                    }
                }

                if (obj["results"] is JArray searchResults && searchResults.Count > 10)
                {
                    int originalCount = searchResults.Count;
                    string currentRaw = obj.ToString(Formatting.None);
                    if (currentRaw.Length > 80000)
                    {
                        // Drastic pruning: keep only first 5
                        while (searchResults.Count > 5) searchResults.RemoveAt(searchResults.Count - 1);
                        obj["isTruncated"] = true;
                        obj["returnedCount"] = 5;
                        obj["originalCount"] = originalCount;
                        return obj;
                    }
                }

                // Intelligent Truncation: Preserve metadata, prune large content
                var fieldsToTruncate = new[] { "source", "content", "code", "fileContent", "details" };
                
                foreach (var field in fieldsToTruncate)
                {
                    var fieldValue = obj[field];
                    if (fieldValue != null && fieldValue.Type == JTokenType.String)
                    {
                        string val = fieldValue.ToString();
                        int fieldBudget = isXmlMetadataRead
                            ? 180000
                            : string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase) ? 12000 : 20000;
                        int headBudget = isXmlMetadataRead
                            ? 140000
                            : string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase) ? 9000 : 15000;
                        int tailBudget = isXmlMetadataRead
                            ? 40000
                            : string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase) ? 3000 : 5000;
                        if (val.Length > fieldBudget)
                        {
                            obj[field] = val.Substring(0, headBudget) + 
                                           "\n\n[... TRUNCATED BY GATEWAY TOKEN BUDGET ...] \n\n" + 
                                           val.Substring(val.Length - tailBudget);
                            obj["isTruncated"] = true;
                        }
                    }
                }
                
                string truncatedRaw = obj.ToString(Formatting.None);
                if (truncatedRaw.Length > 80000)
                {
                    // Fallback to ensuring valid JSON structure when heavily nested Strings overfill
                    return JToken.FromObject(new { 
                        jsonrpc = "2.0",
                        error = "Response exceeded 80k token budget and could not be safely parsed. Try lower limits or pagination.", 
                        isTruncated = true 
                    });
                }
                return obj;
            }
            else if (result is JArray arr)
            {
                // Truncate arrays if they exceed limits
                while (arr.Count > 5 && arr.ToString(Formatting.None).Length > 80000)
                {
                    arr.RemoveAt(arr.Count - 1);
                }
                if (arr.ToString(Formatting.None).Length > 80000)
                {
                    return JToken.FromObject(new { 
                        error = "Array response exceeded 80k token budget. Try lower limits or pagination.", 
                        isTruncated = true 
                    });
                }
                return arr;
            }

            return new JValue(raw.Substring(0, 75000) + "... [TRUNCATED]");
        }

        private static bool IsMutatingTool(string toolName, JObject? args)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;

            if (string.Equals(toolName, "genexus_open_kb", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolName, "genexus_import_object", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (toolName.Contains("write", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("edit", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("patch", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("create", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("refactor", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("add_variable", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(toolName, "genexus_properties", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(args?["action"]?.ToString(), "set", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_asset", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(args?["action"]?.ToString(), "write", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_history", StringComparison.OrdinalIgnoreCase))
            {
                string? action = args?["action"]?.ToString();
                return string.Equals(action, "save", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "restore", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_structure", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(args?["action"]?.ToString(), "update_visual", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase))
            {
                string? action = args?["action"]?.ToString();
                return string.Equals(action, "index", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "reorg", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static JObject BuildToolTextResponse(JToken? idToken, JToken payload, bool isError, string? toolName = null, JObject? toolArgs = null)
        {
            JToken axiPayload = NormalizeToolPayloadForAxi(payload, toolName ?? "unknown", toolArgs, isError);
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idToken?.DeepClone(),
                ["result"] = JToken.FromObject(new
                {
                    content = new[] { new { type = "text", text = axiPayload.ToString(Formatting.None) } },
                    isError
                })
            };
        }

        private static JToken NormalizeToolPayloadForAxi(JToken? payload, string toolName, JObject? toolArgs, bool isError)
        {
            JObject sourceObj;
            if (payload is JArray arrayPayload)
            {
                sourceObj = new JObject
                {
                    ["results"] = arrayPayload.DeepClone()
                };
            }
            else if (payload is JObject objPayload)
            {
                sourceObj = objPayload;
            }
            else
            {
                return payload ?? JValue.CreateNull();
            }

            var obj = (JObject)sourceObj.DeepClone();
            var meta = obj["meta"] as JObject ?? new JObject();
            meta["schemaVersion"] = McpAxiSchemaVersion;
            meta["tool"] = toolName;
            obj["meta"] = meta;
            HashSet<string>? requestedFields = ParseRequestedFields(toolArgs);
            if (requestedFields == null && ShouldUseCompactDefaults(toolArgs))
            {
                requestedFields = GetDefaultCompactFields(toolName);
            }

            if (obj["isTruncated"]?.Value<bool>() == true)
            {
                meta["truncated"] = true;
                var help = obj["help"] as JArray ?? new JArray();
                string truncateHint = string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase)
                    ? "Response truncated by gateway budget. Use limit/offset to page source content."
                    : "Response truncated by gateway budget. Narrow filters or lower limit for deterministic follow-up.";

                if (!help.Any(item => string.Equals(item?.ToString(), truncateHint, StringComparison.OrdinalIgnoreCase)))
                {
                    help.Add(truncateHint);
                }

                obj["help"] = help;
            }

            if (!isError &&
                string.Equals(obj["status"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase) &&
                obj["noChange"] == null &&
                string.Equals(obj["details"]?.ToString(), "No change", StringComparison.OrdinalIgnoreCase))
            {
                obj["noChange"] = true;
            }

            string[] collectionKeys = { "results", "objects", "items", "tools", "checks", "entries" };
            foreach (var key in collectionKeys)
            {
                if (obj[key] is not JArray arr)
                {
                    continue;
                }

                if (requestedFields != null &&
                    requestedFields.Count > 0 &&
                    ShouldProjectFieldsForTool(toolName))
                {
                    obj[key] = ProjectArrayItems(arr, requestedFields);
                    meta["fields"] = new JArray(requestedFields.OrderBy(field => field, StringComparer.OrdinalIgnoreCase));
                    arr = (JArray)obj[key]!;
                }

                if (meta["totalByType"] == null)
                {
                    var totalsByType = BuildTotalsByType(arr);
                    if (totalsByType.Properties().Any())
                    {
                        meta["totalByType"] = totalsByType;
                    }
                }

                int returned = arr.Count;
                if (obj["returned"] == null) obj["returned"] = returned;
                if (obj["empty"] == null) obj["empty"] = returned == 0;
                if ((obj["empty"]?.Value<bool>() ?? false))
                {
                    EnsureEmptyStateHelp(obj, toolName);
                }

                int? total = TryReadInt(obj["total"]) ??
                             TryReadInt(obj["count"]) ??
                             TryReadInt(obj["totalCount"]);
                if (total.HasValue && obj["total"] == null)
                {
                    obj["total"] = total.Value;
                }

                int? limit = TryReadInt(toolArgs?["limit"]);
                int offset = TryReadInt(toolArgs?["offset"]) ?? 0;
                int? effectiveTotal = TryReadInt(obj["total"]);

                if (limit.HasValue && effectiveTotal.HasValue)
                {
                    bool hasMore = (offset + returned) < effectiveTotal.Value;
                    if (obj["hasMore"] == null) obj["hasMore"] = hasMore;
                    if (hasMore && obj["nextOffset"] == null)
                    {
                        obj["nextOffset"] = offset + returned;
                    }
                }

                break;
            }

            return obj;
        }

        private static void EnsureEmptyStateHelp(JObject obj, string toolName)
        {
            var help = obj["help"] as JArray ?? new JArray();
            string hint = string.Equals(toolName, "genexus_query", StringComparison.OrdinalIgnoreCase)
                ? "No matches found for the current query. Try broader terms or remove filters."
                : string.Equals(toolName, "genexus_list_objects", StringComparison.OrdinalIgnoreCase)
                    ? "No objects found for the current scope. Verify parentPath/parent filters."
                    : "No results returned for this request.";

            if (!help.Any(item => string.Equals(item?.ToString(), hint, StringComparison.OrdinalIgnoreCase)))
            {
                help.Add(hint);
            }

            obj["help"] = help;
        }

        private static bool ShouldProjectFieldsForTool(string toolName)
        {
            return string.Equals(toolName, "genexus_query", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(toolName, "genexus_list_objects", StringComparison.OrdinalIgnoreCase);
        }

        private static JObject BuildTotalsByType(JArray arr)
        {
            var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in arr.OfType<JObject>())
            {
                string type = row["type"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(type))
                {
                    continue;
                }

                if (!totals.ContainsKey(type))
                {
                    totals[type] = 0;
                }

                totals[type] += 1;
            }

            var outObj = new JObject();
            foreach (var kv in totals.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                outObj[kv.Key] = kv.Value;
            }

            return outObj;
        }

        private static JArray ProjectArrayItems(JArray arr, HashSet<string> fields)
        {
            var projected = new JArray();
            foreach (var row in arr)
            {
                if (row is not JObject rowObj)
                {
                    projected.Add(row.DeepClone());
                    continue;
                }

                var outRow = new JObject();
                foreach (var field in fields)
                {
                    if (rowObj.TryGetValue(field, StringComparison.OrdinalIgnoreCase, out var value))
                    {
                        outRow[field] = value.DeepClone();
                    }
                }

                projected.Add(outRow);
            }

            return projected;
        }

        private static bool ShouldUseCompactDefaults(JObject? toolArgs)
        {
            if (toolArgs == null) return false;
            var token = toolArgs["axiCompact"];
            if (token == null) return false;
            return token.Type == JTokenType.Boolean
                ? token.Value<bool>()
                : bool.TryParse(token.ToString(), out bool parsed) && parsed;
        }

        private static HashSet<string>? GetDefaultCompactFields(string toolName)
        {
            if (string.Equals(toolName, "genexus_query", StringComparison.OrdinalIgnoreCase))
            {
                return new HashSet<string>(new[] { "name", "type", "path" }, StringComparer.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_list_objects", StringComparison.OrdinalIgnoreCase))
            {
                return new HashSet<string>(new[] { "name", "type", "path", "parentPath" }, StringComparer.OrdinalIgnoreCase);
            }

            return null;
        }

        private static HashSet<string>? ParseRequestedFields(JObject? toolArgs)
        {
            if (toolArgs == null) return null;
            var token = toolArgs["fields"];
            if (token == null) return null;

            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (token.Type == JTokenType.Array)
            {
                foreach (var item in token.Values<string>())
                {
                    if (!string.IsNullOrWhiteSpace(item))
                    {
                        fields.Add(item.Trim());
                    }
                }
            }
            else
            {
                string raw = token.ToString();
                foreach (var piece in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string value = piece.Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        fields.Add(value);
                    }
                }
            }

            return fields.Count == 0 ? null : fields;
        }

        private static int? TryReadInt(JToken? token)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.Integer) return token.Value<int>();
            if (token.Type == JTokenType.Float) return (int)Math.Floor(token.Value<double>());
            if (token.Type == JTokenType.String &&
                int.TryParse(token.Value<string>(), out int parsed))
            {
                return parsed;
            }

            return null;
        }

        private static bool IsOriginAllowed(string? origin, ServerConfig? serverConfig)
        {
            if (string.IsNullOrWhiteSpace(origin)) return true;

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) return false;
            if (originUri.IsLoopback) return true;

            var allowedOrigins = serverConfig?.AllowedOrigins;
            if (allowedOrigins == null || allowedOrigins.Count == 0) return false;

            return allowedOrigins.Any(allowed => string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase));
        }

        private static HttpSessionState CreateHttpSession()
        {
            return _httpSessions.Create();
        }

        private static void QueueSessionMessage(HttpSessionState session, string payload)
        {
            _httpSessions.Enqueue(session, payload);
        }

        private static async Task<IResult> HandleMcpSseStream(HttpContext context)
        {
            var protocolError = McpHttpProtocol.TryApplyProtocol(context.Request, context.Response.Headers);
            if (protocolError != null)
                return Results.Json(new { error = protocolError.Value.Message }, statusCode: protocolError.Value.StatusCode);

            string? sessionId = context.Request.Headers["MCP-Session-Id"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(sessionId))
                return Results.BadRequest(new { error = "Missing MCP-Session-Id header." });

            if (!_httpSessions.TryGet(sessionId, out var session))
                return Results.NotFound(new { error = "Unknown or expired MCP session." });

            if (session == null)
                return Results.NotFound(new { error = "Unknown or expired MCP session." });

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.Headers["Content-Type"] = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["Connection"] = "keep-alive";
            context.Response.Headers["MCP-Session-Id"] = session.Id;

            await context.Response.WriteAsync("retry: 5000\n");
            await context.Response.WriteAsync($"event: session\ndata: {{\"sessionId\":\"{session.Id}\"}}\n\n");
            await context.Response.Body.FlushAsync();

            try
            {
                // Ironclad SSE: No deadline, keep alive indefinitely until client or server disconnects.
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    string? payload = null;
                    lock (session.PendingMessages)
                    {
                        if (session.PendingMessages.Count > 0)
                            payload = session.PendingMessages.Dequeue();
                    }

                    if (payload != null)
                    {
                        string encodedPayload = payload.Replace("\r", "").Replace("\n", "\ndata: ");
                        await context.Response.WriteAsync($"event: message\ndata: {encodedPayload}\n\n");
                        await context.Response.Body.FlushAsync();
                        continue;
                    }

                    try
                    {
                        await context.Response.WriteAsync(": keepalive\n\n");
                        await context.Response.Body.FlushAsync();
                        await Task.Delay(5000, context.RequestAborted);
                    }
                    catch (OperationCanceledException) { break; }
                }
            }
            catch (Exception ex)
            {
                Log($"[HTTP] SSE stream error for session {session.Id}: {ex.Message}");
            }

            return Results.Empty;
        }

        private static async Task<IResult> HandleJsonRpcHttpRequest(HttpRequest request)
        {
            using (var reader = new StreamReader(request.Body))
            {
                string body = await reader.ReadToEndAsync();
                string id = "no-id";

                try
                {
                    var requestObj = JsonConvert.DeserializeObject<JObject>(body);
                    if (requestObj == null) return Results.Json(new { jsonrpc = "2.0", id = (string?)null, error = new { code = -32700, message = "Invalid JSON" } }, statusCode: 400);

                    id = requestObj["id"]?.ToString() ?? "no-id";
                    var sessionError = McpHttpProtocol.TryGetValidSession(_httpSessions, request, requestObj, out var session);
                    if (sessionError != null)
                        return Results.Json(new { jsonrpc = "2.0", id = id, error = new { code = -32001, message = sessionError.Value.Message } }, statusCode: sessionError.Value.StatusCode);

                    var protocolError = McpHttpProtocol.TryApplyProtocol(request, request.HttpContext.Response.Headers);
                    if (protocolError != null)
                        return Results.Json(new { jsonrpc = "2.0", id = id, error = new { code = -32002, message = protocolError.Value.Message } }, statusCode: protocolError.Value.StatusCode);

                    id = requestObj["id"]?.ToString() ?? "no-id";
                    string method = requestObj["method"]?.ToString() ?? "unknown";
                    string bodyBrief = body.Length > 100 ? body.Substring(0, 100) + "..." : body;
                    Log($"[HTTP] Received {method} (ID: {id}) - Body: {bodyBrief}");

                    var response = await ProcessMcpRequest(requestObj);

                    if (McpHttpProtocol.IsInitializeRequest(requestObj))
                    {
                        var newSession = CreateHttpSession();
                        request.HttpContext.Response.Headers["MCP-Session-Id"] = newSession.Id;
                        QueueSessionMessage(newSession, JsonConvert.SerializeObject(new
                        {
                            jsonrpc = "2.0",
                            method = "notifications/message",
                            @params = new
                            {
                                level = "info",
                                logger = "transport",
                                data = "HTTP MCP session initialized."
                            }
                        }));
                    }
                        
                    if (response != null)
                    {
                        Log($"[HTTP] Serializing response for {id}...");
                        string jsonResponse = response.ToString(Formatting.None);
                        Log($"[HTTP] Sending {jsonResponse.Length} bytes to {id}");
                        return Results.Content(jsonResponse, "application/json; charset=utf-8", Encoding.UTF8);
                    }

                    if (requestObj["id"] == null)
                    {
                        Log($"[HTTP] Notification {method} completed without response body.");
                        return Results.NoContent();
                    }

                    return Results.BadRequest(new { error = "No response generated" });
                }
                catch (OperationCanceledException)
                {
                    Log($"[HTTP] Request aborted by client: {id}");
                    return Results.StatusCode(499); // Client Closed Request
                }
                catch (Exception ex)
                {
                    Log($"[HTTP] Error processing {id}: {ex.Message}");
                    return Results.Json(new { jsonrpc = "2.0", id = id, error = new { code = -32603, message = $"Gateway Error: {ex.Message}" } });
                }
            }
        }

        static Task StartHttpServer(Configuration config)
        {
            var serverConfig = config.Server ?? new ServerConfig();
            string bindAddress = string.IsNullOrWhiteSpace(serverConfig.BindAddress) ? "0.0.0.0" : serverConfig.BindAddress;
            Log($"[HTTP] Starting server on {bindAddress}:{serverConfig.HttpPort}...");
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://{bindAddress}:{serverConfig.HttpPort}");
            builder.Logging.ClearProviders();
            builder.Services.AddResponseCompression(options => { options.EnableForHttps = true; });
            var app = builder.Build();
            app.UseResponseCompression();
            _ = Task.Run(() => RunSessionCleanupLoop(app.Lifetime.ApplicationStopping));

            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/mcp"))
                {
                    string? origin = context.Request.Headers["Origin"].FirstOrDefault();
                    if (!IsOriginAllowed(origin, serverConfig))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsync("Origin not allowed.");
                        return;
                    }
                }

                await next();
            });

            app.MapPost("/mcp", async (HttpRequest request) => await HandleJsonRpcHttpRequest(request));
            app.MapGet("/mcp", async (HttpContext context) => await HandleMcpSseStream(context));
            app.MapDelete("/mcp", (HttpRequest request) =>
            {
                var protocolError = McpHttpProtocol.TryApplyProtocol(request, request.HttpContext.Response.Headers);
                if (protocolError != null)
                    return Results.Json(new { error = protocolError.Value.Message }, statusCode: protocolError.Value.StatusCode);

                string? sessionId = request.Headers["MCP-Session-Id"].FirstOrDefault();
                if (string.IsNullOrWhiteSpace(sessionId))
                    return Results.BadRequest(new { error = "Missing MCP-Session-Id header." });

                if (!_httpSessions.Remove(sessionId))
                    return Results.NotFound(new { error = "Unknown or expired MCP session." });

                Log($"[HTTP] Session {sessionId} terminated by client.");
                return Results.NoContent();
            });

            return app.RunAsync();
        }

        private static void TryKillProcessOnPort(int port)
        {
            try {
               Log($"[PortRecovery] Attempting to find process on port {port}...");
               var process = new Process();
               process.StartInfo.FileName = "netstat";
               process.StartInfo.Arguments = "-ano";
               process.StartInfo.RedirectStandardOutput = true;
               process.StartInfo.UseShellExecute = false;
               process.StartInfo.CreateNoWindow = true;
               process.Start();
               string output = process.StandardOutput.ReadToEnd();
               process.WaitForExit();

               var lines = output.Split('\n');
               foreach (var line in lines)
               {
                   if (line.Contains($":{port}") && line.Contains("LISTENING"))
                   {
                       var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                       var pidStr = parts.Last().Trim();
                       if (int.TryParse(pidStr, out int pid) && pid != Environment.ProcessId)
                       {
                           Log($"[PortRecovery] Found zombie process {pid} on port {port}. Killing it...");
                           try {
                               var zombie = Process.GetProcessById(pid);
                               zombie.Kill(true);
                               zombie.WaitForExit(3000);
                           } catch { } // Process might already be gone
                       }
                   }
               }
            } catch (Exception ex) { Log($"[PortRecovery] Error: {ex.Message}"); }
        }
    }
}
