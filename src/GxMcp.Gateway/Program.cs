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

namespace GxMcp.Gateway
{
    class Program
    {
        private static WorkerProcess? _worker;
        private static ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new ConcurrentDictionary<string, TaskCompletionSource<string>>();
        private static ConcurrentDictionary<string, JObject> _semanticCache = new ConcurrentDictionary<string, JObject>();
        private static HttpSessionRegistry _httpSessions = new HttpSessionRegistry(TimeSpan.FromMinutes(10));
        private static readonly string _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gateway_debug.log");
        private static readonly BlockingCollection<string> _logQueue = new BlockingCollection<string>();
        private static readonly string[] _defaultLocalOrigins = new[]
        {
            "http://localhost",
            "http://127.0.0.1",
            "https://localhost",
            "https://127.0.0.1"
        };

        private static void InitializeLogging()
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    if (File.Exists(_logPath))
                    {
                        string prevLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gateway_debug.prev.log");
                        if (File.Exists(prevLog)) File.Delete(prevLog);
                        File.Move(_logPath, prevLog);
                        break;
                    }
                }
                catch 
                { 
                    if (i == 2) break;
                    System.Threading.Thread.Sleep(100); 
                }
            }
            
            // Start background log writer
            Task.Run(() => {
                foreach (var msg in _logQueue.GetConsumingEnumerable())
                {
                    try { File.AppendAllText(_logPath, msg); }
                    catch { }
                }
            });

            Log("=== Gateway starting (Stdio Mode) ===");
        }

        public static void Log(string msg)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            _logQueue.Add($"[{timestamp}] {msg}\n");
        }

        private static async Task RunSessionCleanupLoop(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    int removed = _httpSessions.CleanupExpired();
                    if (removed > 0)
                    {
                        Log($"[HTTP] Removed {removed} expired MCP session(s).");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static void BroadcastNotification(string method, object payload)
        {
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

        static async Task Main(string[] args)
        {
            // Register encoding provider for Windows-1252 support in .NET 8
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            InitializeLogging();

            AppDomain.CurrentDomain.UnhandledException += (s, e) => {
                Log("FATAL UNHANDLED EXCEPTION: " + (e.ExceptionObject as Exception)?.ToString());
            };

            TaskScheduler.UnobservedTaskException += (s, e) => {
                Log("UNOBSERVED TASK EXCEPTION: " + e.Exception?.ToString());
                e.SetObserved();
            };

            Console.Error.WriteLine("=== Gateway starting (Stdio Mode) ===");
            
            var config = Configuration.Load();
            _httpSessions = new HttpSessionRegistry(TimeSpan.FromMinutes(config.Server?.SessionIdleTimeoutMinutes ?? 10));
            
            // Subscribing to Configuration Changes
            Configuration.OnConfigurationChanged += (newConfig) => {
                if (newConfig.Environment?.KBPath != config.Environment?.KBPath || 
                    newConfig.GeneXus?.InstallationPath != config.GeneXus?.InstallationPath) {
                    Log($"[Gateway] Core configuration changed! Restarting Worker process...");
                    config = newConfig; // Update reference
                    RestartWorker(config);
                    BroadcastResourcesListChanged("core_configuration_changed");
                } else {
                    Log($"[Gateway] Minor configuration changed. Ignoring.");
                }
            };

            StartWorker(config);

            // Subscribing to KB changes for Semantic Cache Invalidation
            if (!string.IsNullOrEmpty(config.Environment?.KBPath))
            {
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
                } catch (Exception ex) { Log($"[Cache] Watcher error: {ex.Message}"); }
            }

            // HTTP Server in background
            if (config.Server?.HttpPort > 0)
            {
                _ = Task.Run(async () => {
                    try { await StartHttpServer(config); }
                    catch { }
                });
            }

            // MCP Stdio Loop
            using (var reader = new StreamReader(Console.OpenStandardInput()))
            {
                while (true)
                {
                    string? line = null;
                    try { line = await reader.ReadLineAsync(); } catch { }
                    
                    if (line == null) {
                        // If Stdio is closed but HTTP is enabled, wait forever
                        if (config.Server?.HttpPort > 0) {
                            Log("Stdio closed, keeping alive for HTTP...");
                            await Task.Delay(-1);
                        }
                        break; 
                    }
                    
                    try {
                        var request = JObject.Parse(line);
                        var response = await ProcessMcpRequest(request);
                        if (response != null)
                        {
                            Console.WriteLine(response.ToString(Formatting.None));
                            Console.Out.Flush();
                        }
                    } catch (Exception ex) { Log("MCP Error: " + ex.Message); }
                }
            }
        }

        private static void StartWorker(Configuration config)
        {
            _worker = new WorkerProcess(config);
            _worker.OnRpcResponse += HandleWorkerResponse;
            _worker.OnWorkerExited += () => {
                Log("Worker Process Exited. Notifying all pending requests...");
                foreach (var id in _pendingRequests.Keys)
                {
                    if (_pendingRequests.TryRemove(id, out var tcs))
                    {
                        var errorJson = JsonConvert.SerializeObject(new { 
                            jsonrpc = "2.0", 
                            id = id, 
                            error = new { code = -32603, message = "GeneXus MCP Worker crashed/exited." } 
                        });
                        tcs.TrySetResult(errorJson);
                    }
                }
            };
            _worker.Start();
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
                if (!string.IsNullOrEmpty(id) && _pendingRequests.TryRemove(id, out var tcs))
                    tcs.SetResult(json);
            } catch { }
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
            Func<JObject> onTimeout)
        {
            string requestId = Guid.NewGuid().ToString();
            var tcs = new TaskCompletionSource<string>();
            _pendingRequests[requestId] = tcs;

            var workerRequest = BuildWorkerRpcRequest(workerCommand, requestId);
            await _worker!.SendCommandAsync(workerRequest.ToString(Formatting.None));

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completedTask == tcs.Task)
            {
                return onSuccess(JObject.Parse(await tcs.Task));
            }

            _pendingRequests.TryRemove(requestId, out _);
            Log(timeoutLogMessage);
            return onTimeout();
        }

        private static async Task<JObject?> ProcessMcpRequest(JObject request)
        {
            string? method = request["method"]?.ToString();
            var idToken = request["id"];

            // Protocol level
            var mcpResponse = McpRouter.Handle(request);
            if (mcpResponse != null)
            {
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
                var workerCmd = McpRouter.ConvertResourceCall(request) as JObject;
                if (workerCmd != null)
                {
                    workerCmd["client"] = "mcp";
                    return await SendWorkerCommandAsync(
                        workerCmd,
                        60000,
                        $"Timeout waiting for resource: {request["params"]?["uri"]}",
                        resultObj =>
                        {
                            var content = resultObj["result"]?.ToString() ?? "";
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
                        () => new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idToken?.DeepClone(),
                            ["error"] = JToken.FromObject(new { code = -32603, message = "GeneXus MCP Worker timed out reading resource." })
                        });
                }
            }

            // Tool Calls (Actual logic)
            if (method == "tools/call")
            {
                var paramsObj = request["params"] as JObject;
                string toolName = paramsObj?["name"]?.ToString() ?? "";
                var args = paramsObj?["arguments"] as JObject;
                
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

                var rawWorkerCmd = McpRouter.ConvertToolCall(request);
                var workerCmd = rawWorkerCmd != null ? JObject.FromObject(rawWorkerCmd) : null;
                if (workerCmd != null)
                {
                    workerCmd["client"] = "mcp";

                    int timeoutMs = 60000;
                    if (toolName == "genexus_lifecycle" || toolName == "genexus_analyze" || toolName == "genexus_test")
                        timeoutMs = 300000; // 5 minutes for heavy operations

                    return await SendWorkerCommandAsync(
                        workerCmd,
                        timeoutMs,
                        $"Timeout waiting for tool: {toolName}",
                        resultObj =>
                        {
                            var finalResult = TruncateResponseIfNeeded(resultObj["result"] ?? resultObj["error"], toolName);

                            var response = new JObject
                            {
                                ["jsonrpc"] = "2.0",
                                ["id"] = idToken?.DeepClone(),
                                ["result"] = JToken.FromObject(new
                                {
                                    content = new[] { new { type = "text", text = finalResult.ToString() } },
                                    isError = resultObj["error"] != null
                                })
                            };

                            if (resultObj["error"] == null && !toolName.Contains("write") && !toolName.Contains("patch"))
                            {
                                _semanticCache[cacheKey] = response;
                            }

                            return response;
                        },
                        () => new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idToken?.DeepClone(),
                            ["error"] = JToken.FromObject(new { code = -32603, message = $"GeneXus MCP Worker timed out executing tool: {toolName}" })
                        });
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

        private static JToken TruncateResponseIfNeeded(JToken? result, string toolName)
        {
            if (result == null) return JValue.CreateNull();
            
            string raw = result.ToString(Formatting.None);
            if (raw.Length < 60000) return result;

            Log($"[Budget] Truncating response for {toolName} ({raw.Length} chars)");

            if (result is JObject obj)
            {
                if (obj["results"] is JArray searchResults)
                {
                    int originalCount = searchResults.Count;
                    while (searchResults.Count > 1 && obj.ToString(Formatting.None).Length > 79000)
                    {
                        searchResults.RemoveAt(searchResults.Count - 1);
                    }

                    if (searchResults.Count < originalCount)
                    {
                        obj["isTruncated"] = true;
                        obj["returnedCount"] = searchResults.Count;

                        while (searchResults.Count > 1 && obj.ToString(Formatting.None).Length > 80000)
                        {
                            searchResults.RemoveAt(searchResults.Count - 1);
                            obj["returnedCount"] = searchResults.Count;
                        }
                    }

                    if (obj.ToString(Formatting.None).Length <= 80000)
                    {
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
                        if (val.Length > 20000)
                        {
                            obj[field] = val.Substring(0, 15000) + 
                                           "\n\n[... TRUNCATED BY GATEWAY TOKEN BUDGET ...] \n\n" + 
                                           val.Substring(val.Length - 5000);
                            obj["isTruncated"] = true;
                        }
                    }
                }
                
                string truncatedRaw = obj.ToString(Formatting.None);
                if (truncatedRaw.Length > 80000)
                {
                    // Fallback to ensuring valid JSON structure when heavily nested Strings overfill
                    return JToken.FromObject(new { 
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

            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            try
            {
                while (!context.RequestAborted.IsCancellationRequested && DateTime.UtcNow < deadline)
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

                    await context.Response.WriteAsync(": keepalive\n\n");
                    await context.Response.Body.FlushAsync();
                    try
                    {
                        await Task.Delay(5000, context.RequestAborted);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }
            }
            catch (IOException)
            {
                Log($"[HTTP] SSE stream closed for session {session.Id}.");
            }
            catch (OperationCanceledException)
            {
                Log($"[HTTP] SSE stream canceled for session {session.Id}.");
            }

            return Results.Empty;
        }

        private static async Task<IResult> HandleJsonRpcHttpRequest(HttpRequest request)
        {
            using (var reader = new StreamReader(request.Body))
            {
                string body = await reader.ReadToEndAsync();

                try
                {
                    var requestObj = JsonConvert.DeserializeObject<JObject>(body);
                    if (requestObj == null) return Results.BadRequest(new { error = "Invalid JSON" });

                    var sessionError = McpHttpProtocol.TryGetValidSession(_httpSessions, request, requestObj, out var session);
                    if (sessionError != null)
                        return Results.Json(new { error = sessionError.Value.Message }, statusCode: sessionError.Value.StatusCode);

                    var protocolError = McpHttpProtocol.TryApplyProtocol(request, request.HttpContext.Response.Headers);
                    if (protocolError != null)
                        return Results.Json(new { error = protocolError.Value.Message }, statusCode: protocolError.Value.StatusCode);

                    string method = requestObj["method"]?.ToString() ?? "unknown";
                    string id = requestObj["id"]?.ToString() ?? "no-id";
                    Log($"[HTTP] Received {method} (ID: {id}) on /mcp");

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
                        Log($"[HTTP] Responding to {id}");
                        return Results.Content(response.ToString(Formatting.None), "application/json");
                    }

                    return Results.BadRequest(new { error = "No response generated" });
                }
                catch (Exception ex)
                {
                    Log($"[HTTP Error] {ex.Message}");
                    return Results.Problem(ex.Message);
                }
            }
        }

        static Task StartHttpServer(Configuration config)
        {
            var serverConfig = config.Server ?? new ServerConfig();
            string bindAddress = string.IsNullOrWhiteSpace(serverConfig.BindAddress) ? "127.0.0.1" : serverConfig.BindAddress;
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
    }
}
