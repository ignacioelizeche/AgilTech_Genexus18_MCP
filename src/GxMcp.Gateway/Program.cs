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

        // Dedicated MCP output stream - the ONLY writer to stdout
        private static StreamWriter _mcpOut = null!;

        // Debug log path relative to exe directory
        private static readonly string _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcp_debug.log");

        public static void Log(string msg)
        {
            try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
            catch { /* logging must never crash the server */ }
        }

        static async Task Main(string[] args)
        {
            Log("=== Gateway starting ===");

            // 1. Capture the raw stdout stream EXCLUSIVELY for MCP protocol
            _mcpOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };

            // 2. Redirect Console.Out to stderr so ANY library (ASP.NET, Kestrel, etc.)
            //    that accidentally calls Console.WriteLine() will NOT pollute the MCP channel
            var stderrWriter = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetOut(stderrWriter);

            Log("stdout/stderr redirected, starting stdio loop");

            InitializeBackgroundServices();
            
            await RunStdioLoop();
        }

        static void InitializeBackgroundServices()
        {
            try 
            {
                Log("Background init: loading config...");
                var config = Configuration.Load();
                Log($"Config loaded. Worker={config.GeneXus?.WorkerExecutable}");
                
                try
                {
                    Log($"Starting worker process from: {config.GeneXus?.WorkerExecutable}");
                    var worker = new WorkerProcess(config);
                    worker.OnRpcResponse += HandleWorkerResponse;
                    worker.Start();
                    _worker = worker;
                    Log("Worker started successfully");
                }
                catch (Exception wex)
                {
                    Log($"Worker start CRITICAL FAILED: {wex.Message}\nStack: {wex.StackTrace}");
                }

                // HTTP Server DISABLED for MCP-only mode — avoids port conflicts
                // Uncomment when running with Dashboard:
                // _ = Task.Run(async () => {
                //     try { await StartHttpServer(config); }
                //     catch { /* HTTP is optional */ }
                // });
                Log("Background init complete (HTTP server disabled for MCP mode)");
            }
            catch (Exception ex)
            {
                Log($"Background init FAILED: {ex.Message}");
                Console.Error.WriteLine($"[Gateway] Background init failed: {ex.Message}");
            }
        }

        private static void HandleWorkerResponse(string json)
        {
            try 
            {
                var val = JObject.Parse(json);
                string id = val["id"]?.ToString();
                if (!string.IsNullOrEmpty(id) && _pendingRequests.TryRemove(id, out var tcs))
                {
                    tcs.SetResult(json);
                }
                else
                {
                    Console.Error.WriteLine($"[Worker Response] {json}");
                }
            }
            catch (Exception ex) 
            {
                Console.Error.WriteLine($"[Gateway JSON Parse Error] {ex.Message}");
            }
        }

        static async Task RunStdioLoop()
        {
            using (var reader = new StreamReader(Console.OpenStandardInput()))
            {
                while (true)
                {
                    string line = await reader.ReadLineAsync();
                    if (line == null) break; 
                    
                    try 
                    {
                        if (!line.TrimStart().StartsWith("{")) continue;

                        var request = JObject.Parse(line);
                        var idToken = request["id"]; // Preserve original JToken type (number, string, etc.)
                        string idStr = idToken?.ToString(); // For log/dictionary lookups only
                        string method = request["method"]?.ToString();

                        Log($"Received: method={method} id={idStr}");

                        // 1. Notifications (no response expected by MCP spec)
                        if (method != null && method.StartsWith("notifications/"))
                        {
                            Log($"Notification ignored: {method}");
                            continue;
                        }

                        // 2. Handle Protocol Messages (Initialize, List Tools, Ping)
                        var mcpResponse = McpRouter.Handle(request);
                        if (mcpResponse != null)
                        {
                            // Build response as JObject to preserve id token type
                            var response = new JObject
                            {
                                ["jsonrpc"] = "2.0",
                                ["id"] = idToken?.DeepClone(),
                                ["result"] = JToken.FromObject(mcpResponse)
                            };
                            var json = response.ToString(Formatting.None);
                            Log($"Responding to {method}: {json.Substring(0, Math.Min(json.Length, 200))}");
                            _mcpOut.WriteLine(json);
                            continue;
                        }
                        
                        // 3. Handle Tool Calls (Translation)
                        if (method == "tools/call")
                        {
                            var workerCmd = McpRouter.ConvertToolCall(request);
                            if (workerCmd != null)
                            {
                                if (_worker == null)
                                {
                                    var errResponse = new JObject
                                    {
                                        ["jsonrpc"] = "2.0",
                                        ["id"] = idToken?.DeepClone(),
                                        ["result"] = JToken.FromObject(new 
                                        {
                                            content = new[] 
                                            {
                                                new { type = "text", text = "Worker is still initializing, please try again in a few seconds." }
                                            },
                                            isError = true
                                        })
                                    };
                                    _mcpOut.WriteLine(errResponse.ToString(Formatting.None));
                                    continue;
                                }

                                var tcs = new TaskCompletionSource<string>();
                                _pendingRequests[idStr] = tcs;

                                var rpcWrapper = new {
                                    jsonrpc = "2.0",
                                    id = idStr,
                                    method = "execute_command",
                                    @params = workerCmd
                                };
                                
                                await _worker.SendCommandAsync(JsonConvert.SerializeObject(rpcWrapper));
                                
                                // Wait for response with timeout
                                var timeoutTask = Task.Delay(30000);
                                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                                if (completedTask == timeoutTask)
                                {
                                    _pendingRequests.TryRemove(idStr, out _);
                                    var timeoutResponse = new JObject
                                    {
                                        ["jsonrpc"] = "2.0",
                                        ["id"] = idToken?.DeepClone(),
                                        ["result"] = JToken.FromObject(new 
                                        {
                                            content = new[] 
                                            {
                                                new { type = "text", text = "{\"error\": \"Timeout waiting for Worker (30s)\"}" }
                                            },
                                            isError = true
                                        })
                                    };
                                    _mcpOut.WriteLine(timeoutResponse.ToString(Formatting.None));
                                    continue;
                                }

                                string workerResultJson = await tcs.Task;
                                var workerResultObj = JObject.Parse(workerResultJson);
                                
                                var finalResult = workerResultObj["result"] ?? workerResultObj["error"];
                                string resultText = finalResult?.ToString() ?? "{\"error\": \"Worker returned an empty or null result.\"}";

                                var mcpToolResponse = new JObject
                                {
                                    ["jsonrpc"] = "2.0",
                                    ["id"] = idToken?.DeepClone(),
                                    ["result"] = JToken.FromObject(new 
                                    {
                                        content = new[] 
                                        {
                                            new { type = "text", text = resultText }
                                        },
                                        isError = workerResultObj["error"] != null
                                    })
                                };
                                
                                _mcpOut.WriteLine(mcpToolResponse.ToString(Formatting.None));
                                continue;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[Gateway Stdio Error] {ex.Message}");
                    }
                }
            }
        }

        static Task StartHttpServer(Configuration config)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://*:{config.Server.HttpPort}");
            builder.Logging.ClearProviders();
            
            // Add CORS
            builder.Services.AddCors(options => {
                options.AddPolicy("AllowAll", builder => 
                    builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
            });

            var app = builder.Build();
            app.UseCors("AllowAll");

            // API Routes matching the Dashboard expectations
            app.MapPost("/api/command", async (HttpRequest request) => {
                using (var reader = new StreamReader(request.Body))
                {
                    string body = await reader.ReadToEndAsync();
                    
                    // Gateway generates ID
                    string requestId = Guid.NewGuid().ToString();

                    var rpcWrapper = new {
                        jsonrpc = "2.0",
                        id = requestId,
                        method = "execute_command",
                        @params = JsonConvert.DeserializeObject(body)
                    };
                    
                    var tcs = new TaskCompletionSource<string>();
                    _pendingRequests[requestId] = tcs;

                    await (_worker?.SendCommandAsync(JsonConvert.SerializeObject(rpcWrapper)) ?? Task.CompletedTask);

                    // Wait for response (Timeout 30s)
                    var timeoutTask = Task.Delay(30000);
                    var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                    if (completedTask == timeoutTask)
                    {
                        _pendingRequests.TryRemove(requestId, out _);
                        return Results.Json(new { error = "Timeout waiting for Worker" }, statusCode: 504);
                    }

                    string resultJson = await tcs.Task;
                    
                    try 
                    {
                        var resultObj = JObject.Parse(resultJson);
                        // Convert result to string if it is an object, or pass through
                        var inner = resultObj["result"];
                        // If inner is object, ToString() gives JSON. If string, gives string.
                        // We want to return JSON.
                        return Results.Content(inner.ToString(), "application/json");
                    }
                    catch
                    {
                        return Results.Content(resultJson, "application/json");
                    }
                }
            });

            app.MapGet("/api/ping", () => Results.Ok(new { online = true, gateway = "GxMcp.Gateway v2.0" }));

            return app.RunAsync();
        }
    }
}
