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
                case "genexus_open_kb":
                    return new
                    {
                        module = "KB",
                        action = "Open",
                        target = args?["path"]?.ToString()
                    };

                case "genexus_read":
                {
                    var targetsTokRead = args?["targets"];
                    bool hasTargetsRead = targetsTokRead is JArray;
                    bool hasNameRead = !string.IsNullOrEmpty(target);
                    if (hasNameRead && hasTargetsRead)
                        throw new UsageException("usage_error", "name and targets are mutually exclusive");
                    if (hasTargetsRead)
                    {
                        return new {
                            module = "Batch",
                            action = "BatchRead",
                            items = (JArray)targetsTokRead!,
                            part = part
                        };
                    }
                    return new {
                        module = "Read",
                        action = "ExtractSource",
                        target = target,
                        part = part,
                        offset = args?["offset"]?.ToObject<int?>(),
                        limit = args?["limit"]?.ToObject<int?>(),
                        type = args?["type"]?.ToString()
                    };
                }

                case "genexus_edit":
                {
                    if (args?["changes"] != null)
                        throw new UsageException("usage_error", "argument 'changes' removed in v2.0.0; use 'targets' instead");

                    var targetsTokEdit = args?["targets"];
                    bool hasTargetsEdit = targetsTokEdit is JArray;
                    bool hasNameEdit = !string.IsNullOrEmpty(target);
                    if (hasNameEdit && hasTargetsEdit)
                        throw new UsageException("usage_error", "name and targets are mutually exclusive");
                    if (hasTargetsEdit)
                    {
                        return new {
                            module = "Batch",
                            action = "MultiEdit",
                            items = (JArray)targetsTokEdit!,
                            dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false
                        };
                    }

                    string? mode = args?["mode"]?.ToString();
                    if (mode == "ops")
                    {
                        return new {
                            module = "SemanticOps",
                            action = "Apply",
                            target = target,
                            part = part,
                            ops = args?["ops"],
                            dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false
                        };
                    }
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
                            expectedCount = args?["expectedCount"]?.ToObject<int?>() ?? 1,
                            dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                            verifyRollback = args?["verifyRollback"]?.ToObject<bool?>() ?? false
                        };
                    }
                    else
                    {
                        return new {
                            module = "Write",
                            action = part,
                            target = target,
                            payload = args?["content"]?.ToString(),
                            type = args?["type"]?.ToString(),
                            dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false
                        };
                    }
                }

                // Aliases legados (escondidos mas funcionais para a Gateway interna se necessário)
                case "genexus_read_source":
                    return new { module = "Read", action = "ExtractSource", target = target, part = part };
                case "genexus_patch":
                    return new {
                        module = "Patch",
                        action = "Apply",
                        target = target,
                        part = part,
                        operation = args?["operation"]?.ToString(),
                        content = args?["content"]?.ToString(),
                        context = args?["context"]?.ToString(),
                        expectedCount = args?["expectedCount"]?.ToObject<int?>() ?? 1,
                        dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                        verifyRollback = args?["verifyRollback"]?.ToObject<bool?>() ?? false
                    };
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

                case "genexus_export_object":
                    return new
                    {
                        module = "Object",
                        action = "ExportText",
                        target = target,
                        outputPath = args?["outputPath"]?.ToString(),
                        part = part,
                        type = args?["type"]?.ToString(),
                        overwrite = args?["overwrite"]?.ToObject<bool?>() ?? false
                    };

                case "genexus_import_object":
                    return new
                    {
                        module = "Object",
                        action = "ImportText",
                        target = target,
                        inputPath = args?["inputPath"]?.ToString(),
                        part = part,
                        type = args?["type"]?.ToString()
                    };

                default:
                    return null;
            }
        }
    }
}
