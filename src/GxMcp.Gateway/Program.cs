using System;
using System.IO;
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
        private static readonly string _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gateway_debug.log");
        private static readonly BlockingCollection<string> _logQueue = new BlockingCollection<string>();

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

        private static void HandleWorkerResponse(string json)
        {
            try {
                var val = JObject.Parse(json);
                string id = val["id"]?.ToString();
                if (!string.IsNullOrEmpty(id) && _pendingRequests.TryRemove(id, out var tcs))
                    tcs.SetResult(json);
            } catch { }
        }

        private static async Task<JObject?> ProcessMcpRequest(JObject request)
        {
            string method = request["method"]?.ToString();
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
                    string idStr = Guid.NewGuid().ToString();
                    var tcs = new TaskCompletionSource<string>();
                    _pendingRequests[idStr] = tcs;

                    var rpcWrapper = new { jsonrpc = "2.0", id = idStr, method = "execute_command", @params = workerCmd };
                    await _worker!.SendCommandAsync(JsonConvert.SerializeObject(rpcWrapper));

                    var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(60000));
                    if (completedTask == tcs.Task)
                    {
                        var resultObj = JObject.Parse(await tcs.Task);
                        var content = resultObj["result"]?.ToString() ?? "";
                        return new JObject { 
                            ["jsonrpc"] = "2.0", 
                            ["id"] = idToken?.DeepClone(), 
                            ["result"] = JToken.FromObject(new { 
                                contents = new[] { 
                                    new { 
                                        uri = request["params"]?["uri"]?.ToString(), 
                                        mimeType = "text/plain", 
                                        text = content 
                                    } 
                                } 
                            }) 
                        };
                    }
                    else
                    {
                        Log($"Timeout waiting for resource: {request["params"]?["uri"]}");
                        return new JObject { 
                            ["jsonrpc"] = "2.0", 
                            ["id"] = idToken?.DeepClone(), 
                            ["error"] = JToken.FromObject(new { code = -32603, message = "GeneXus MCP Worker timed out reading resource." }) 
                        };
                    }
                }
            }

            // Tool Calls (Actual logic)
            if (method == "tools/call")
            {
                var paramsObj = request["params"] as JObject;
                string toolName = paramsObj?["name"]?.ToString() ?? "";
                var args = paramsObj?["arguments"] as JObject;
                
                // 1. CACHE INVALIDATION: If it's a write operation or a re-index, clear the cache
                if (toolName.Contains("write") || toolName.Contains("patch") || toolName.Contains("bulk_index") || toolName.Contains("batch_edit"))
                {
                    Log($"[Cache] Invalidation triggered by {toolName}");
                    _semanticCache.Clear();
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
                    string idStr = Guid.NewGuid().ToString();
                    var tcs = new TaskCompletionSource<string>();
                    _pendingRequests[idStr] = tcs;

                    var rpcWrapper = new { jsonrpc = "2.0", id = idStr, method = "execute_command", @params = workerCmd };
                    await _worker!.SendCommandAsync(JsonConvert.SerializeObject(rpcWrapper));

                    var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(60000));
                    if (completedTask == tcs.Task)
                    {
                        string resultJson = await tcs.Task;
                        var resultObj = JObject.Parse(resultJson);
                        var finalResult = TruncateResponseIfNeeded(resultObj["result"] ?? resultObj["error"], toolName);
                        
                        var response = new JObject { 
                            ["jsonrpc"] = "2.0", 
                            ["id"] = idToken?.DeepClone(), 
                            ["result"] = JToken.FromObject(new { content = new[] { new { type = "text", text = finalResult.ToString() } }, isError = resultObj["error"] != null }) 
                        };

                        // Store in cache if not an error and not a write tool
                        if (resultObj["error"] == null && !toolName.Contains("write") && !toolName.Contains("patch"))
                        {
                            _semanticCache[cacheKey] = response;
                        }

                        return response;
                    }
                    else
                    {
                        Log($"Timeout waiting for tool: {toolName}");
                        return new JObject { 
                            ["jsonrpc"] = "2.0", 
                            ["id"] = idToken?.DeepClone(), 
                            ["error"] = JToken.FromObject(new { code = -32603, message = $"GeneXus MCP Worker timed out executing tool: {toolName}" }) 
                        };
                    }
                }
            }
            

            // 4. Compatibility: execute_command (Direct Worker Dispatch)
            if (method == "execute_command")
            {
                string idStr = Guid.NewGuid().ToString();
                var tcs = new TaskCompletionSource<string>();
                _pendingRequests[idStr] = tcs;

                var rpcWrapper = new { jsonrpc = "2.0", id = idStr, method = "execute_command", @params = request["params"] };
                await _worker!.SendCommandAsync(JsonConvert.SerializeObject(rpcWrapper));

                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(30000));
                if (completedTask == tcs.Task)
                {
                    var resultJson = await tcs.Task;
                    var resultObj = JObject.Parse(resultJson);
                    // Match the original ID from request if present
                    if (idToken != null) resultObj["id"] = idToken.DeepClone();
                    return resultObj;
                }
                else
                {
                    Log($"Timeout waiting for execute_command");
                    return new JObject { 
                        ["jsonrpc"] = "2.0", 
                        ["id"] = idToken?.DeepClone(), 
                        ["error"] = JToken.FromObject(new { code = -32603, message = "GeneXus MCP Worker timed out executing direct command." }) 
                    };
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
                // Intelligent Truncation: Preserve metadata, prune large content
                if (obj["source"] != null && obj["source"].Type == JTokenType.String)
                {
                    string source = obj["source"].ToString();
                    if (source.Length > 30000)
                    {
                        obj["source"] = source.Substring(0, 25000) + 
                                       "\n\n[... TRUNCATED BY GATEWAY TOKEN BUDGET ...] \n\n" + 
                                       source.Substring(source.Length - 5000);
                        obj["isTruncated"] = true;
                    }
                }
                
                string truncatedRaw = obj.ToString(Formatting.None);
                if (truncatedRaw.Length > 80000)
                {
                    return new JValue(truncatedRaw.Substring(0, 75000) + "... [HARD TRUNCATED]");
                }
                return obj;
            }

            return new JValue(raw.Substring(0, 75000) + "... [TRUNCATED]");
        }

        static Task StartHttpServer(Configuration config)
        {
            Log($"[HTTP] Starting server on port {config.Server.HttpPort}...");
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://*:{config.Server.HttpPort}");
            builder.Logging.ClearProviders();
            builder.Services.AddResponseCompression(options => { options.EnableForHttps = true; });
            builder.Services.AddCors(options => options.AddPolicy("AllowAll", b => b.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));
            var app = builder.Build();
            app.UseResponseCompression();
            app.UseCors("AllowAll");

            app.MapPost("/api/command", async (HttpRequest request) => {
                using (var reader = new StreamReader(request.Body)) {
                    string body = await reader.ReadToEndAsync();
                    
                    try {
                        var requestObj = JsonConvert.DeserializeObject<JObject>(body);
                        if (requestObj == null) return Results.BadRequest(new { error = "Invalid JSON" });

                        string method = requestObj["method"]?.ToString() ?? "unknown";
                        string id = requestObj["id"]?.ToString() ?? "no-id";
                        Log($"[HTTP] Received {method} (ID: {id})");

                        var response = await ProcessMcpRequest(requestObj);
                        
                        if (response != null) {
                            Log($"[HTTP] Responding to {id}");
                            return Results.Content(response.ToString(Formatting.None), "application/json");
                        }
                        return Results.BadRequest(new { error = "No response generated" });
                    } catch (Exception ex) {
                        Log($"[HTTP Error] {ex.Message}");
                        return Results.Problem(ex.Message);
                    }
                }
            });

            return app.RunAsync();
        }
    }
}
