using System;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace GxMcp.Gateway
{
    public class McpRouter
    {
        public static object Handle(JObject request)
        {
            string method = request["method"]?.ToString();
            switch (method)
            {
                case "initialize":
                    return new {
                        protocolVersion = "2025-03-26",
                        capabilities = new { tools = new { listChanged = true } },
                        serverInfo = new { name = "genexus-mcp-server", version = "3.5.0" }
                    };
                case "tools/list":
                    return new { tools = GetToolDefinitions() };
                case "notifications/initialized": return null;
                case "ping": return new { };
                default: return null;
            }
        }

        private static object[] GetToolDefinitions()
        {
            return new object[]
            {
                new {
                    name = "genexus_list_objects",
                    description = "Fast KB discovery. Unified search by Name, Type (Procedure, Transaction, WebPanel), or Description. Returns enriched results with 'parm' rules and code snippets.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            filter = new { type = "string", description = "Search term (e.g. 'Prc:MyProc', 'Customer', 'Transaction')." },
                            limit = new { type = "integer", description = "Maximum objects to return.", @default = 50 }
                        }
                    }
                },
                new {
                    name = "genexus_search",
                    description = "Advanced semantic search and impact analysis. Use 'usedby:TableName' to find all references. Returns connection counts, 'parm' signatures, and source snippets.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            query = new { type = "string", description = "Search query or 'usedby:TargetName'." }
                        },
                        required = new[] { "query" }
                    }
                },
                new {
                    name = "genexus_read_object",
                    description = "Reads full object metadata (GUID, Type, and all available Parts list). Use for structural inspection.",
                    inputSchema = new {
                        type = "object",
                        properties = new { name = new { type = "string", description = "Object name." } },
                        required = new[] { "name" }
                    }
                },
                new {
                    name = "genexus_read_source",
                    description = "Reads source code with SMART context. Automatically includes metadata for variables used in the snippet. Supports pagination via offset/limit to save tokens.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Object name." },
                            part = new { type = "string", description = "Part (Source, Rules, Events).", @default = "Source" },
                            offset = new { type = "integer", description = "Start line (0-based) for pagination." },
                            limit = new { type = "integer", description = "Number of lines to read." }
                        },
                        required = new[] { "name" }
                    }
                },
                new {
                    name = "genexus_write_object",
                    description = "Directly updates object source code. Use with caution for large objects. Automatically handles variable injection based on KB attributes.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Object name." },
                            part = new { type = "string", description = "Part name.", @default = "Source" },
                            code = new { type = "string", description = "Full source code to write." }
                        },
                        required = new[] { "name", "code" }
                    }
                },
                new {
                    name = "genexus_patch",
                    description = "Surgical text editing. High-precision replacement or insertion within an object part. Use unique 'context' (old_string) to target specific lines safely. Preserves indentation.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Object name." },
                            part = new { type = "string", description = "Part name (Source, Rules, Events).", @default = "Source" },
                            operation = new { type = "string", description = "Operation: Replace, Insert_After, Append." },
                            content = new { type = "string", description = "New text to insert or replacement text." },
                            context = new { type = "string", description = "The exact 'old_string' to match. Must be unique unless expectedCount is set." },
                            expectedCount = new { type = "integer", description = "Number of replacements expected. Defaults to 1.", @default = 1 }
                        },
                        required = new[] { "name", "operation", "content" }
                    }
                },
                new {
                    name = "genexus_validate",
                    description = "Surgical syntax check using native SDK engine. Call before saving to ensure logic is sound. Returns SDK diagnostics.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Object name." },
                            part = new { type = "string", description = "Part name.", @default = "Source" },
                            code = new { type = "string", description = "Code to validate." }
                        },
                        required = new[] { "name", "code" }
                    }
                },
                new {
                    name = "genexus_analyze",
                    description = "Deep static analysis: identifies complexity, COMMIT in loops, unfiltered loops, and business logic insights.",
                    inputSchema = new {
                        type = "object",
                        properties = new { name = new { type = "string", description = "Object name." } },
                        required = new[] { "name" }
                    }
                },
                new {
                    name = "genexus_test",
                    description = "Executes GXtest Unit Tests via native runner. Returns real-time feedback and assertion results.",
                    inputSchema = new {
                        type = "object",
                        properties = new { name = new { type = "string", description = "Unit Test object name." } },
                        required = new[] { "name" }
                    }
                },
                new {
                    name = "genexus_scaffold",
                    description = "Creates a new object from template (CRUD Procedure or Transaction). Streamlines common patterns.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            type = new { type = "string", description = "Prc or Trn." },
                            name = new { type = "string", description = "New object name." },
                            description = new { type = "string", description = "Object description." },
                            code = new { type = "string", description = "Initial code." }
                        },
                        required = new[] { "type", "name" }
                    }
                },
                new {
                    name = "genexus_get_ui_context",
                    description = "UI Vision: returns control list and layout structure for Web Panels and Transactions.",
                    inputSchema = new {
                        type = "object",
                        properties = new { name = new { type = "string", description = "Object name." } },
                        required = new[] { "name" }
                    }
                },
                new {
                    name = "genexus_get_data_context",
                    description = "Holistic Data Vision: returns base table, extended table, and parent/child relationships for an object.",
                    inputSchema = new {
                        type = "object",
                        properties = new { name = new { type = "string", description = "Object name." } },
                        required = new[] { "name" }
                    }
                },
                new {
                    name = "genexus_build",
                    description = "Executes Build operations (Build, Sync, Reorg). High-level compile task.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            action = new { type = "string", description = "Build, RebuildAll, Sync, Reorg." },
                            target = new { type = "string", description = "Environment or Object name." }
                        },
                        required = new[] { "action" }
                    }
                },
                new {
                    name = "genexus_get_variables",
                    description = "Lists all variables defined in an object, including their types.",
                    inputSchema = new {
                        type = "object",
                        properties = new { name = new { type = "string", description = "Object name." } },
                        required = new[] { "name" }
                    }
                },
                new {
                    name = "genexus_get_attribute",
                    description = "Returns metadata for a specific GeneXus attribute (Type, Length, Domain, Table).",
                    inputSchema = new {
                        type = "object",
                        properties = new { name = new { type = "string", description = "Attribute name." } },
                        required = new[] { "name" }
                    }
                },
                new {
                    name = "genexus_get_hierarchy",
                    description = "Retrieves the call tree (who calls this, who is called by this).",
                    inputSchema = new {
                        type = "object",
                        properties = new { name = new { type = "string" } },
                        required = new[] { "name" }
                    }
                },
                new {
                    name = "genexus_visualize",
                    description = "Generates an interactive HTML dependency graph for a domain or the whole KB.",
                    inputSchema = new {
                        type = "object",
                        properties = new { domain = new { type = "string", description = "Optional domain filter." } }
                    }
                },
                new {
                    name = "genexus_health_report",
                    description = "Holistic KB health monitoring: dead code, circular dependencies, complexity hotspots.",
                    inputSchema = new { type = "object", properties = new { } }
                },
                new {
                    name = "genexus_linter",
                    description = "Analyzes object source code for anti-patterns (commits in loops, unfiltered loops, etc).",
                    inputSchema = new {
                        type = "object",
                        properties = new { name = new { type = "string", description = "Object name to lint." } },
                        required = new[] { "name" }
                    }
                },
                new {
                    name = "genexus_get_pattern_sample",
                    description = "Returns a representative object of a given type to serve as a coding style reference.",
                    inputSchema = new {
                        type = "object",
                        properties = new { type = new { type = "string", description = "Type of object (e.g. Transaction, Procedure)." } },
                        required = new[] { "type" }
                    }
                },
                new {
                    name = "genexus_doctor",
                    description = "Analyzes build logs to diagnose errors and suggest fixes. Use this when builds fail.",
                    inputSchema = new {
                        type = "object",
                        properties = new { logPath = new { type = "string", description = "Optional path to MSBuild log file." } }
                    }
                },
                new {
                    name = "genexus_wiki",
                    description = "Generates technical documentation in Wiki format for the object.",
                    inputSchema = new {
                        type = "object",
                        properties = new { name = new { type = "string" } },
                        required = new[] { "name" }
                    }
                },
                new {
                    name = "genexus_history",
                    description = "Lists object revision history or restores a previous version.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string" },
                            action = new { type = "string", description = "List, Restore" }
                        },
                        required = new[] { "name", "action" }
                    }
                },
                new {
                    name = "genexus_bulk_index",
                    description = "Full KB crawl to rebuild the search index. Mandatory after large changes. Updates 'parm' rules and snippets.",
                    inputSchema = new { type = "object", properties = new { } }
                }
            };
        }

        public static object ConvertToolCall(JObject request)
        {
            string method = request["method"]?.ToString();
            if (method != "tools/call") return null;
            var paramsObj = request["params"] as JObject;
            string toolName = paramsObj?["name"]?.ToString();
            var args = paramsObj?["arguments"] as JObject;

            switch (toolName)
            {
                case "genexus_list_objects":
                case "genexus_search":
                    string q = args?["query"]?.ToString() ?? args?["filter"]?.ToString() ?? "";
                    return new { module = "Search", action = "Query", target = q, limit = args?["limit"]?.ToObject<int?>() ?? 50 };
                case "genexus_read_object":
                    return new { module = "Read", action = "Export", target = args?["name"]?.ToString() };
                case "genexus_read_source":
                    return new { 
                        module = "Read", 
                        action = "ExtractSource", 
                        target = args?["name"]?.ToString(), 
                        part = args?["part"]?.ToString() ?? "Source",
                        offset = args?["offset"]?.ToObject<int?>(),
                        limit = args?["limit"]?.ToObject<int?>()
                    };
                case "genexus_validate":
                    return new {
                        module = "Validation",
                        action = "Check",
                        target = args?["name"]?.ToString(),
                        part = args?["part"]?.ToString() ?? "Source",
                        payload = args?["code"]?.ToString()
                    };
                case "genexus_test":
                    return new {
                        module = "Test",
                        action = "Run",
                        target = args?["name"]?.ToString()
                    };
                case "genexus_scaffold":
                    return new {
                        module = "Forge",
                        action = args?["type"]?.ToString(),
                        target = args?["name"]?.ToString(),
                        payload = args?["code"]?.ToString(),
                        description = args?["description"]?.ToString()
                    };
                case "genexus_write_object":
                    return new { module = "Write", action = args?["part"]?.ToString() ?? "Source", target = args?["name"]?.ToString(), payload = args?["code"]?.ToString() };
                case "genexus_analyze":
                    return new { module = "Analyze", action = "Analyze", target = args?["name"]?.ToString() };
                case "genexus_get_data_context":
                    return new { module = "Analyze", action = "GetDataContext", target = args?["name"]?.ToString() };
                case "genexus_get_ui_context":
                    return new { module = "UI", action = "GetUIContext", target = args?["name"]?.ToString() };
                case "genexus_get_variables":
                    return new { module = "Read", action = "GetVariables", target = args?["name"]?.ToString() };
                case "genexus_get_attribute":
                    return new { module = "Read", action = "GetAttribute", target = args?["name"]?.ToString() };
                case "genexus_build":
                    return new { module = "Build", action = args?["action"]?.ToString(), target = args?["target"]?.ToString() };
                case "genexus_get_hierarchy":
                    return new { module = "Analyze", action = "GetHierarchy", target = args?["name"]?.ToString() };
                case "genexus_visualize":
                    return new { module = "Visualizer", action = "Generate", target = args?["domain"]?.ToString() };
                case "genexus_health_report":
                    return new { module = "Health", action = "GetReport" };
                case "genexus_linter":
                    return new { module = "Linter", action = "Analyze", target = args?["name"]?.ToString() };
                case "genexus_get_pattern_sample":
                    return new { module = "Pattern", action = "GetSample", target = args?["type"]?.ToString() };
                case "genexus_patch":
                    return new { 
                        module = "Patch", 
                        action = "Apply", 
                        target = args?["name"]?.ToString(),
                        part = args?["part"]?.ToString() ?? "Source",
                        operation = args?["operation"]?.ToString(),
                        content = args?["content"]?.ToString(),
                        context = args?["context"]?.ToString(),
                        expectedCount = args?["expectedCount"]?.ToObject<int?>() ?? 1
                    };
                case "genexus_doctor":
                    return new { module = "Doctor", action = "Diagnose", target = args?["logPath"]?.ToString() };
                case "genexus_wiki":
                    return new { module = "Wiki", action = "Generate", target = args?["name"]?.ToString() };
                case "genexus_history":
                    return new { module = "History", action = args?["action"]?.ToString(), target = args?["name"]?.ToString() };
                case "genexus_bulk_index":
                    return new { module = "KB", action = "BulkIndex" };
                default:
                    return null;
            }
        }
    }
}
