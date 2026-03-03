using Newtonsoft.Json.Linq;
using System;

namespace GxMcp.Gateway.Routers
{
    public class ObjectRouter : IMcpModuleRouter
    {
        public string ModuleName => "Object";

        public object[] GetToolDefinitions()
        {
            return new object[]
            {
                new {
                    name = "genexus_read",
                    description = "Reads source code from specific object parts (Source, Rules, Events). Supports pagination.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Object name." },
                            part = new { type = "string", description = "Part (Source, Rules, Events).", @default = "Source" },
                            offset = new { type = "integer", description = "Start line for pagination." },
                            limit = new { type = "integer", description = "Lines to read." }
                        },
                        required = new[] { "name" }
                    }
                },
                new {
                    name = "genexus_edit",
                    description = "Updates object source code. Use 'mode=full' for complete overwrite or 'mode=patch' for surgical changes.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Object name." },
                            part = new { type = "string", description = "Part name (e.g. Source, Rules).", @default = "Source" },
                            mode = new { type = "string", @enum = new[] { "full", "patch" }, description = "Edit mode." },
                            content = new { type = "string", description = "New code or patch content." },
                            context = new { type = "string", description = "For patch mode: The exact text (old_string) to replace." },
                            operation = new { type = "string", @enum = new[] { "Replace", "Insert_After", "Append" }, description = "For patch mode: Operation to perform." },
                            changes = new {
                                type = "array",
                                items = new {
                                    type = "object",
                                    properties = new {
                                        part = new { type = "string" },
                                        mode = new { type = "string", @enum = new[] { "full", "patch" } },
                                        content = new { type = "string" },
                                        context = new { type = "string" },
                                        operation = new { type = "string", @enum = new[] { "Replace", "Insert_After", "Append" } }
                                    },
                                    required = new[] { "content" }
                                },
                                description = "Batch of changes to apply to this object."
                            }
                        },
                        required = new[] { "name" }
                    }
                },
                new {
                    name = "genexus_batch_edit",
                    description = "Applies edits across multiple objects in a single call. Each item specifies a target object and an array of changes (patch or full). More efficient than multiple genexus_edit calls.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            items = new {
                                type = "array",
                                items = new {
                                    type = "object",
                                    properties = new {
                                        name = new { type = "string", description = "Target object name." },
                                        changes = new {
                                            type = "array",
                                            items = new {
                                                type = "object",
                                                properties = new {
                                                    part = new { type = "string", description = "Part name (Source, Rules, Events).", @default = "Source" },
                                                    mode = new { type = "string", @enum = new[] { "full", "patch" }, description = "Edit mode.", @default = "patch" },
                                                    content = new { type = "string", description = "New code or patch content." },
                                                    context = new { type = "string", description = "For patch mode: exact text to replace." },
                                                    operation = new { type = "string", @enum = new[] { "Replace", "Insert_After", "Append" }, description = "Patch operation.", @default = "Replace" }
                                                },
                                                required = new[] { "content" }
                                            },
                                            description = "Changes to apply to this object."
                                        }
                                    },
                                    required = new[] { "name", "changes" }
                                },
                                description = "Array of objects to edit, each with its own changes."
                            }
                        },
                        required = new[] { "items" }
                    }
                }
            };
        }

        public object ConvertToolCall(string toolName, JObject args)
        {
            string target = args?["name"]?.ToString();
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

                    string mode = args?["mode"]?.ToString();
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

                default:
                    return null;
            }
        }
    }
}
