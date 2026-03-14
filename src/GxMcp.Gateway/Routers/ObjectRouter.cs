using Newtonsoft.Json.Linq;
namespace GxMcp.Gateway.Routers
{
    public class ObjectRouter : IMcpModuleRouter
    {
        public string ModuleName => "Object";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            string? target = args?["name"]?.ToString();
            string part = args?["part"]?.ToString() ?? "Source";

            switch (toolName)
            {
                case "genexus_read":
                    return new { 
                        module = "Read", 
                        action = "ExtractSource", 
                        target = target, 
                        part = part,
                        offset = args?["offset"]?.ToObject<int?>(),
                        limit = args?["limit"]?.ToObject<int?>()
                    };

                case "genexus_edit":
                    if (args?["changes"] != null)
                    {
                        return new { 
                            module = "Batch", 
                            action = "BatchEdit", 
                            target = target,
                            changes = args["changes"]
                        };
                    }

                    string? mode = args?["mode"]?.ToString();
                    if (mode == "patch")
                    {
                        return new { 
                            module = "Patch", 
                            action = "Apply", 
                            target = target,
                            part = part,
                            operation = args?["operation"]?.ToString() ?? "Replace",
                            content = args?["content"]?.ToString(),
                            context = args?["context"]?.ToString(),
                            expectedCount = 1
                        };
                    }
                    else
                    {
                        return new { module = "Write", action = part, target = target, payload = args?["content"]?.ToString() };
                    }
                
                // Aliases legados (escondidos mas funcionais para a Gateway interna se necessário)
                case "genexus_read_source":
                    return new { module = "Read", action = "ExtractSource", target = target, part = part };
                case "genexus_patch":
                    return new { module = "Patch", action = "Apply", target = target, part = part, operation = args?["operation"]?.ToString(), content = args?["content"]?.ToString(), context = args?["context"]?.ToString() };
                case "genexus_write_object":
                    return new { module = "Write", action = part, target = target, payload = args?["code"]?.ToString() };
                case "genexus_get_variables":
                    return new { module = "Read", action = "GetVariables", target = target };
                case "genexus_get_attribute":
                    return new { module = "Read", action = "GetAttribute", target = target };
                case "genexus_get_properties":
                    return new { module = "Property", action = "Get", target = target, control = args?["control"]?.ToString() };

                case "genexus_batch_edit":
                    return new { module = "Batch", action = "MultiEdit", items = args?["items"] };

                case "genexus_batch_read":
                    return new { module = "Batch", action = "BatchRead", items = args?["items"] };

                default:
                    return null;
            }
        }
    }
}
