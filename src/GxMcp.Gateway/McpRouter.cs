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
                    description = "Fast KB discovery. Use 'filter' for Type (Procedure, Transaction, WebPanel) or Name.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            filter = new { type = "string", description = "Type or partial name." },
                            limit = new { type = "integer", @default = 50 }
                        }
                    }
                },
                new {
                    name = "genexus_search",
                    description = "Advanced semantic search. Finds objects by name, description, or business domain (acad, fin, etc).",
                    inputSchema = new {
                        type = "object",
                        properties = new { query = new { type = "string", description = "Search term." } },
                        required = new[] { "query" }
                    }
                },
                new {
                    name = "genexus_read_source",
                    description = "Reads source code. Use 'Source' for main code, 'Rules', 'Events', or 'Variables'.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Object name (e.g. 'Prc:MyProc')." },
                            part = new { type = "string", description = "Part name.", @default = "Source" }
                        },
                        required = new[] { "name" }
                    }
                },
                new {
                    name = "genexus_read_object",
                    description = "Reads full object metadata (GUID, Type, All Parts).",
                    inputSchema = new {
                        type = "object",
                        properties = new { name = new { type = "string" } },
                        required = new[] { "name" }
                    }
                },
                new {
                    name = "genexus_write_object",
                    description = "Updates object source code. Use with caution.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string" },
                            part = new { type = "string", @default = "Source" },
                            code = new { type = "string" }
                        },
                        required = new[] { "name", "code" }
                    }
                },
                new {
                    name = "genexus_get_ui_context",
                    description = "UI Vision: returns control list and layout structure for Web Panels and Transactions.",
                    inputSchema = new {
                        type = "object",
                        properties = new { name = new { type = "string", description = "Object name (e.g. 'Wp:MyWebPanel')." } },
                        required = new[] { "name" }
                    }
                },
                new {
                    name = "genexus_get_data_context",
                    description = "Holistic Data Vision: returns base table, extended table, and parent/child relationships for an object.",
                    inputSchema = new {
                        type = "object",
                        properties = new { name = new { type = "string", description = "Object name (e.g. 'Trn:Aluno' or 'Prc:MyProc')." } },
                        required = new[] { "name" }
                    }
                },
                new {
                    name = "genexus_analyze",
                    description = "Deep static analysis: finds all calls, tables used, and business logic insights.",
                    inputSchema = new {
                        type = "object",
                        properties = new { name = new { type = "string" } },
                        required = new[] { "name" }
                    }
                },
                new {
                    name = "genexus_build",
                    description = "Executes GeneXus Build operations (Build, RebuildAll, Sync, Reorg).",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            action = new { type = "string", description = "Build, RebuildAll, Sync, Reorg" },
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
                        properties = new { name = new { type = "string", description = "Object name (e.g. 'Prc:MyProc')." } },
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
                        properties = new { domain = new { type = "string", description = "Optional domain filter (e.g. Financeiro)" } }
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
                    name = "genexus_patch_object",
                    description = "Surgical text editing for large objects. Supports Append, Prepend, Replace, Insert_After.",
                    inputSchema = new {
                        type = "object",
                        properties = new { 
                            name = new { type = "string", description = "Object name." },
                            part = new { type = "string", description = "Part name (Source, Rules, Events)." },
                            operation = new { type = "string", description = "Operation: Append, Prepend, Replace, Insert_After." },
                            content = new { type = "string", description = "Text to insert/replace." },
                            context = new { type = "string", description = "Anchor text for Replace/Insert_After." }
                        },
                        required = new[] { "name", "part", "operation", "content" }
                    }
                },
                new {
                    name = "genexus_doctor",
                    description = "Calculates the 'Blast Radius' of a change. Returns all transitively affected objects and risk score.",
                    inputSchema = new {
                        type = "object",
                        properties = new { name = new { type = "string", description = "Object name." } },
                        required = new[] { "name" }
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
                    description = "Rebuilds the local search cache. Run after large changes.",
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
                    return new { module = "ListObjects", action = "List", target = args?["filter"]?.ToString() ?? "", limit = args?["limit"]?.ToObject<int?>() ?? 50 };
                case "genexus_search":
                    return new { module = "Search", action = "Query", target = args?["query"]?.ToString() };
                case "genexus_read_object":
                    return new { module = "Read", action = "Export", target = args?["name"]?.ToString() };
                case "genexus_read_source":
                    return new { module = "Read", action = "ExtractSource", target = args?["name"]?.ToString(), part = args?["part"]?.ToString() ?? "Source" };
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
                case "genexus_patch_object":
                    return new { 
                        module = "Patch", 
                        action = "Apply", 
                        target = args?["name"]?.ToString(),
                        part = args?["part"]?.ToString(),
                        operation = args?["operation"]?.ToString(),
                        content = args?["content"]?.ToString(),
                        context = args?["context"]?.ToString()
                    };
                case "genexus_impact_analysis":
                    return new { module = "Analyze", action = "ImpactAnalysis", target = args?["name"]?.ToString() };
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
