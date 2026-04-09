using Newtonsoft.Json.Linq;
namespace GxMcp.Gateway.Routers
{
    public class SystemRouter : IMcpModuleRouter
    {
        public string ModuleName => "System";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            string? action = args?["action"]?.ToString();
            string? target = args?["target"]?.ToString();

            switch (toolName)
            {
                case "genexus_lifecycle":
                    switch (action)
                    {
                        case "build": return new { module = "Build", action = "Build", target = target };
                        case "rebuild": return new { module = "Build", action = "RebuildAll", target = target };
                        case "reorg": return new { module = "Build", action = "Reorg", target = target };
                        case "validate": return new { module = "Validation", action = "Check", target = target, payload = args?["code"]?.ToString() };
                        case "sync": return new { module = "Build", action = "Sync", target = target };
                        case "index": return new { module = "KB", action = "BulkIndex" };
                        case "status": 
                            if (!string.IsNullOrEmpty(target))
                                return new { module = "Build", action = "Status", target = target };
                            return new { module = "KB", action = "GetIndexStatus" };
                        case "result":
                            if (!string.IsNullOrEmpty(target))
                                return new { module = "Build", action = "Status", target = target };
                            return null;
                        default: return null;
                    }

                case "genexus_forge":
                    switch (action)
                    {
                        case "scaffold":
                            return new {
                                module = "Forge",
                                action = "Scaffold",
                                type = args?["type"]?.ToString(),
                                name = args?["name"]?.ToString(),
                                code = args?["content"]?.ToString(),
                                description = args?["description"]?.ToString()
                            };
                        case "translate":
                            return new {
                                module = "Conversion",
                                action = "TranslateTo",
                                target = args?["name"]?.ToString(),
                                language = args?["content"]?.ToString()
                            };
                        case "sample": return new { module = "Pattern", action = "GetSample", target = args?["type"]?.ToString() };
                        default: return null;
                    }

                case "genexus_doc":
                    switch (action)
                    {
                        case "wiki": return new { module = "Wiki", action = "Generate", target = target };
                        case "visualize": return new { module = "Visualizer", action = "Generate", target = target };
                        case "health": return new { module = "Health", action = "GetReport" };
                        default: return null;
                    }

                case "genexus_test":
                    return new { module = "Test", action = "Run", target = args?["name"]?.ToString() };
                
                // Legados
                case "genexus_validate":
                    return new { module = "Validation", action = "Check", target = target, payload = args?["code"]?.ToString() };
                case "genexus_build":
                    return new { module = "Build", action = args?["action"]?.ToString(), target = target };
                case "genexus_history":
                    return new {
                        module = "History",
                        action = args?["action"]?.ToString(),
                        target = args?["name"]?.ToString(),
                        versionId = args?["versionId"]?.ToObject<int?>()
                    };

                default:
                    return null;
            }
        }
    }
}
