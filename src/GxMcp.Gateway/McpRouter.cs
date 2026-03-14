using System;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using GxMcp.Gateway.Routers;

namespace GxMcp.Gateway
{
    public class McpRouter
    {
        public const string ServerVersion = "1.0.0";
        public const string SupportedProtocolVersion = "2025-06-18";
        private static readonly string[] _objectParts = { "Source", "Rules", "Events", "Variables", "Structure", "Layout" };
        private static readonly string[] _analysisIncludes = { "metadata", "variables", "signature", "structure" };
        private static readonly string[] _targetLanguages = { "CSharp", "TypeScript", "Java", "Python" };
        private static readonly string[] _promptNames =
        {
            "gx_explain_object",
            "gx_convert_object",
            "gx_review_transaction",
            "gx_refactor_procedure",
            "gx_generate_tests",
            "gx_trace_dependencies"
        };
        private static readonly List<IMcpModuleRouter> _routers;
        private static JArray _toolDefinitions = new JArray();

        static McpRouter()
        {
            _routers = new List<IMcpModuleRouter>
            {
                new SearchRouter(),
                new ObjectRouter(),
                new AnalyzeRouter(),
                new SystemRouter(),
                new OperationsRouter()
            };

            LoadToolDefinitions();
        }

        private static void LoadToolDefinitions()
        {
            try
            {
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                string defPath = Path.Combine(exeDir, "tool_definitions.json");
                if (File.Exists(defPath))
                {
                    string json = File.ReadAllText(defPath);
                    _toolDefinitions = JArray.Parse(json);
                    Program.Log($"[McpRouter] Loaded {_toolDefinitions.Count} tool definitions from JSON.");
                }
                else
                {
                    Program.Log($"[McpRouter] ERROR: tool_definitions.json not found at {defPath}");
                }
            }
            catch (Exception ex)
            {
                Program.Log($"[McpRouter] ERROR loading tool definitions: {ex.Message}");
            }
        }

        public static object? Handle(JObject request)
        {
            string? method = request["method"]?.ToString();
            switch (method)
            {
                case "initialize":
                    return new
                    {
                        protocolVersion = SupportedProtocolVersion,
                        capabilities = new
                        {
                            prompts = new { listChanged = false },
                            tools = new { listChanged = true },
                            resources = new { listChanged = true, subscribe = true },
                            completion = new { }
                        },
                        serverInfo = new { name = "genexus-mcp-server", version = ServerVersion }
                    };
                case "tools/list":
                    return new { tools = _toolDefinitions };
                case "resources/list":
                    return new
                    {
                        resources = new[]
                        {
                            new { uri = "genexus://kb/index-status", name = "KB Index Status", description = "Current indexing status for the active Knowledge Base." },
                            new { uri = "genexus://kb/health", name = "Gateway Health Report", description = "Health report for the GeneXus MCP worker and gateway." },
                            new { uri = "genexus://objects", name = "GeneXus Objects Index", description = "Browsable index of all objects in the KB." },
                            new { uri = "genexus://attributes", name = "GeneXus Attributes", description = "Browsable list of all attributes." }
                        }
                    };
                case "resources/read":
                    return null;
                case "resources/templates/list":
                    return new
                    {
                        resourceTemplates = new[]
                        {
                            new
                            {
                                uriTemplate = "genexus://objects/{name}/part/{part}",
                                name = "GeneXus Object Part",
                                description = "Read a specific part of a GeneXus object such as Source, Rules, Events, Variables, Structure, or Layout."
                            },
                            new
                            {
                                uriTemplate = "genexus://objects/{name}/variables",
                                name = "GeneXus Object Variables",
                                description = "Read the variable declarations for a GeneXus object."
                            },
                            new
                            {
                                uriTemplate = "genexus://objects/{name}/navigation",
                                name = "GeneXus Navigation",
                                description = "Read the navigation analysis for a GeneXus object."
                            },
                            new
                            {
                                uriTemplate = "genexus://objects/{name}/hierarchy",
                                name = "GeneXus Hierarchy",
                                description = "Read the dependency hierarchy for a GeneXus object."
                            },
                            new
                            {
                                uriTemplate = "genexus://objects/{name}/data-context",
                                name = "GeneXus Data Context",
                                description = "Read attributes, variables, and inferred data context for a GeneXus object."
                            },
                            new
                            {
                                uriTemplate = "genexus://objects/{name}/ui-context",
                                name = "GeneXus UI Context",
                                description = "Read UI structure and controls for a GeneXus object."
                            },
                            new
                            {
                                uriTemplate = "genexus://objects/{name}/conversion-context",
                                name = "GeneXus Conversion Context",
                                description = "Read consolidated conversion context for a GeneXus object."
                            },
                            new
                            {
                                uriTemplate = "genexus://objects/{name}/pattern-metadata",
                                name = "GeneXus Pattern Metadata",
                                description = "Read pattern metadata detected for a GeneXus object."
                            },
                            new
                            {
                                uriTemplate = "genexus://objects/{name}/summary",
                                name = "GeneXus Object Summary",
                                description = "Read an LLM-oriented summary for a GeneXus object."
                            },
                            new
                            {
                                uriTemplate = "genexus://objects/{name}/indexes",
                                name = "GeneXus Visual Indexes",
                                description = "Read visual indexes for a Transaction or Table."
                            },
                            new
                            {
                                uriTemplate = "genexus://objects/{name}/logic-structure",
                                name = "GeneXus Logic Structure",
                                description = "Read the logical structure for a Transaction or Table."
                            },
                            new
                            {
                                uriTemplate = "genexus://attributes/{name}",
                                name = "GeneXus Attribute Metadata",
                                description = "Read metadata for a specific GeneXus attribute."
                            }
                        }
                    };
                case "completion/complete":
                    return HandleCompletion(request);
                case "prompts/list":
                    return new { prompts = BuildPromptCatalog() };
                case "prompts/get":
                    return BuildPromptResponse(request);
                case "notifications/initialized":
                    return null;
                case "ping":
                    return new { };
                default:
                    return null;
            }
        }

        private static object HandleCompletion(JObject request)
        {
            var paramsObj = request["params"] as JObject;
            var argument = paramsObj?["argument"] as JObject;
            string argumentName = argument?["name"]?.ToString() ?? "";
            string currentValue = argument?["value"]?.ToString() ?? "";
            string refType = paramsObj?["ref"]?["type"]?.ToString() ?? "";
            string refName = paramsObj?["ref"]?["name"]?.ToString() ?? "";
            string uriTemplate = paramsObj?["ref"]?["uriTemplate"]?.ToString() ?? "";

            IEnumerable<string> values = Enumerable.Empty<string>();

            if (argumentName == "part")
            {
                values = _objectParts;
            }
            else if (argumentName == "language" || argumentName == "targetLanguage")
            {
                values = _targetLanguages;
            }
            else if (argumentName == "include")
            {
                values = _analysisIncludes;
            }
            else if (argumentName == "prompt")
            {
                values = _promptNames;
            }
            else if (refType == "ref/resource")
            {
                if (uriTemplate.Contains("/part/{part}", StringComparison.OrdinalIgnoreCase))
                    values = _objectParts;
                else if (uriTemplate.Contains("/conversion-context", StringComparison.OrdinalIgnoreCase))
                    values = _analysisIncludes;
            }
            else if (refType == "ref/tool")
            {
                if (refName == "genexus_read")
                    values = _objectParts;
                else if (refName == "genexus_inspect")
                    values = _analysisIncludes;
                else if (refName == "genexus_forge")
                    values = _targetLanguages;
                else if (refName == "genexus_lifecycle")
                    values = new[] { "build", "rebuild", "reorg", "validate", "sync", "index", "status" };
                else if (refName == "genexus_properties")
                    values = new[] { "get", "set" };
                else if (refName == "genexus_history")
                    values = new[] { "list", "get_source", "save", "restore" };
                else if (refName == "genexus_structure")
                    values = new[] { "get_visual", "update_visual", "get_indexes", "get_logic" };
                else if (refName == "genexus_refactor")
                    values = new[] { "RenameAttribute", "RenameVariable", "RenameObject", "ExtractProcedure" };
                else if (refName == "prompts/get")
                    values = _promptNames;
            }

            var filteredValues = values
                .Where(value => value.StartsWith(currentValue, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(value => new { value })
                .ToArray();

            return new
            {
                completion = new
                {
                    values = filteredValues
                }
            };
        }

        private static object[] BuildPromptCatalog()
        {
            return new object[]
            {
                new
                {
                    name = "gx_explain_object",
                    description = "Explain a GeneXus object using source, variables, navigation, and summary context.",
                    arguments = new object[]
                    {
                        new { name = "name", description = "GeneXus object name.", required = true },
                        new { name = "part", description = "Primary part to emphasize during the explanation.", required = false }
                    }
                },
                new
                {
                    name = "gx_convert_object",
                    description = "Prepare a GeneXus object for conversion to another language using conversion context and target-specific guidance.",
                    arguments = new object[]
                    {
                        new { name = "name", description = "GeneXus object name.", required = true },
                        new { name = "targetLanguage", description = "Target language for conversion.", required = true }
                    }
                },
                new
                {
                    name = "gx_review_transaction",
                    description = "Review a Transaction object with focus on structure, rules, and generated impact.",
                    arguments = new object[]
                    {
                        new { name = "name", description = "Transaction object name.", required = true }
                    }
                },
                new
                {
                    name = "gx_refactor_procedure",
                    description = "Refactor a Procedure with attention to readability, side effects, and migration safety.",
                    arguments = new object[]
                    {
                        new { name = "name", description = "Procedure object name.", required = true }
                    }
                },
                new
                {
                    name = "gx_generate_tests",
                    description = "Generate a test plan from source, variables, navigation, and business context.",
                    arguments = new object[]
                    {
                        new { name = "name", description = "GeneXus object name.", required = true }
                    }
                },
                new
                {
                    name = "gx_trace_dependencies",
                    description = "Trace upstream and downstream dependencies for a GeneXus object.",
                    arguments = new object[]
                    {
                        new { name = "name", description = "GeneXus object name.", required = true }
                    }
                }
            };
        }

        private static object BuildPromptResponse(JObject request)
        {
            var paramsObj = request["params"] as JObject;
            string promptName = paramsObj?["name"]?.ToString() ?? "";
            var args = paramsObj?["arguments"] as JObject ?? new JObject();

            return promptName switch
            {
                "gx_explain_object" => new
                {
                    description = "Explain a GeneXus object with grounded context.",
                    messages = new[]
                    {
                        CreatePromptMessage(BuildExplainObjectPrompt(
                            args["name"]?.ToString() ?? "",
                            args["part"]?.ToString() ?? "Source"))
                    }
                },
                "gx_convert_object" => new
                {
                    description = "Guide an object conversion workflow with explicit review gates.",
                    messages = new[]
                    {
                        CreatePromptMessage(BuildConvertObjectPrompt(
                            args["name"]?.ToString() ?? "",
                            args["targetLanguage"]?.ToString() ?? "CSharp"))
                    }
                },
                "gx_review_transaction" => new
                {
                    description = "Review a Transaction object for correctness and migration readiness.",
                    messages = new[]
                    {
                        CreatePromptMessage(BuildReviewTransactionPrompt(args["name"]?.ToString() ?? ""))
                    }
                },
                "gx_refactor_procedure" => new
                {
                    description = "Refactor a Procedure while preserving behavior.",
                    messages = new[]
                    {
                        CreatePromptMessage(BuildRefactorProcedurePrompt(args["name"]?.ToString() ?? ""))
                    }
                },
                "gx_generate_tests" => new
                {
                    description = "Generate a test plan for a GeneXus object.",
                    messages = new[]
                    {
                        CreatePromptMessage(BuildGenerateTestsPrompt(args["name"]?.ToString() ?? ""))
                    }
                },
                "gx_trace_dependencies" => new
                {
                    description = "Trace dependencies and impact for a GeneXus object.",
                    messages = new[]
                    {
                        CreatePromptMessage(BuildTraceDependenciesPrompt(args["name"]?.ToString() ?? ""))
                    }
                },
                _ => new
                {
                    description = "Unknown prompt.",
                    messages = new[]
                    {
                        CreatePromptMessage($"Prompt '{promptName}' is not defined by this server.")
                    }
                }
            };
        }

        private static object CreatePromptMessage(string text)
        {
            return new
            {
                role = "user",
                content = new
                {
                    type = "text",
                    text
                }
            };
        }

        private static string BuildExplainObjectPrompt(string name, string part)
        {
            return
                $"Explain the GeneXus object '{name}'. " +
                $"Start from resource 'genexus://objects/{name}/part/{part}', then use 'genexus://objects/{name}/variables', " +
                $"'genexus://objects/{name}/navigation', and 'genexus://objects/{name}/summary'. " +
                "Summarize purpose, data flow, external dependencies, and risky assumptions. " +
                "If important context is missing, say exactly which additional resource should be read next.";
        }

        private static string BuildConvertObjectPrompt(string name, string targetLanguage)
        {
            return
                $"Prepare the GeneXus object '{name}' for conversion to {targetLanguage}. " +
                $"Read 'genexus://objects/{name}/conversion-context', 'genexus://objects/{name}/variables', " +
                $"'genexus://objects/{name}/navigation', and 'genexus://objects/{name}/summary' first. " +
                "Produce: semantic summary, target architecture assumptions, unsupported features, manual review items, and a translation plan. " +
                "Do not invent framework behavior that is not grounded in the retrieved context.";
        }

        private static string BuildReviewTransactionPrompt(string name)
        {
            return
                $"Review the Transaction '{name}'. " +
                $"Read 'genexus://objects/{name}/part/Structure', 'genexus://objects/{name}/part/Rules', " +
                $"'genexus://objects/{name}/data-context', and 'genexus://objects/{name}/summary'. " +
                "Focus on data integrity, inferred business rules, side effects, and migration risks. " +
                "Report findings first, then open questions, then recommended changes.";
        }

        private static string BuildRefactorProcedurePrompt(string name)
        {
            return
                $"Refactor the Procedure '{name}' without changing behavior. " +
                $"Read 'genexus://objects/{name}/part/Source', 'genexus://objects/{name}/variables', " +
                $"'genexus://objects/{name}/navigation', and 'genexus://objects/{name}/summary'. " +
                "Identify duplicated logic, implicit dependencies, and extraction opportunities. " +
                "Return a stepwise refactor plan before proposing code changes.";
        }

        private static string BuildGenerateTestsPrompt(string name)
        {
            return
                $"Generate a test plan for the GeneXus object '{name}'. " +
                $"Ground the analysis in 'genexus://objects/{name}/summary', 'genexus://objects/{name}/variables', " +
                $"'genexus://objects/{name}/navigation', and the primary source part under 'genexus://objects/{name}/part/Source'. " +
                "List normal cases, edge cases, integration dependencies, and regression risks. " +
                "Prefer deterministic assertions over vague behavioral checks.";
        }

        private static string BuildTraceDependenciesPrompt(string name)
        {
            return
                $"Trace dependencies for the GeneXus object '{name}'. " +
                $"Use 'genexus://objects/{name}/hierarchy', 'genexus://objects/{name}/navigation', " +
                $"'genexus://objects/{name}/summary', and if needed 'genexus_query' with 'usedby:{name}'. " +
                "Separate direct dependencies, indirect dependencies, and likely impact zones. " +
                "Call out where the trace is inferred versus explicitly grounded in retrieved data.";
        }

        public static object? ConvertResourceCall(JObject request)
        {
            string uri = request["params"]?["uri"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(uri)) return null;

            if (uri == "genexus://kb/index-status") return new { module = "KB", action = "GetIndexStatus" };
            if (uri == "genexus://kb/health") return new { module = "Health", action = "GetReport" };
            if (uri == "genexus://objects") return new { module = "Search", action = "Query", target = "", limit = 200 };
            if (uri == "genexus://attributes") return new { module = "Search", action = "Query", target = "type:Attribute", limit = 200 };

            if (TryReadObjectResource(uri, out var objectResource))
                return objectResource;

            if (uri.StartsWith("genexus://attributes/", StringComparison.OrdinalIgnoreCase))
            {
                string name = uri.Replace("genexus://attributes/", "");
                return new { module = "Read", action = "GetAttribute", target = name };
            }

            return null;
        }

        private static bool TryReadObjectResource(string uri, out object? resourceCall)
        {
            resourceCall = null;
            const string objectPrefix = "genexus://objects/";
            if (!uri.StartsWith(objectPrefix, StringComparison.OrdinalIgnoreCase)) return false;

            string relativePath = uri.Substring(objectPrefix.Length).Trim('/');
            if (string.IsNullOrWhiteSpace(relativePath)) return false;

            string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return false;

            string name = segments[0];
            if (string.IsNullOrWhiteSpace(name)) return false;

            if (segments.Length == 1)
            {
                resourceCall = new { module = "Read", action = "ExtractSource", target = name, part = "Source" };
                return true;
            }

            string resourceKind = segments[1];
            switch (resourceKind.ToLowerInvariant())
            {
                case "part":
                    string part = segments.Length >= 3 ? segments[2] : "Source";
                    resourceCall = new { module = "Read", action = "ExtractSource", target = name, part };
                    return true;
                case "source":
                    resourceCall = new { module = "Read", action = "ExtractSource", target = name, part = "Source" };
                    return true;
                case "variables":
                    resourceCall = new { module = "Read", action = "GetVariables", target = name };
                    return true;
                case "navigation":
                    resourceCall = new { module = "Analyze", action = "GetNavigation", target = name };
                    return true;
                case "hierarchy":
                    resourceCall = new { module = "Analyze", action = "GetHierarchy", target = name };
                    return true;
                case "data-context":
                    resourceCall = new { module = "Analyze", action = "GetDataContext", target = name };
                    return true;
                case "ui-context":
                    resourceCall = new { module = "UI", action = "GetUIContext", target = name };
                    return true;
                case "conversion-context":
                    resourceCall = new { module = "Analyze", action = "GetConversionContext", target = name };
                    return true;
                case "pattern-metadata":
                    resourceCall = new { module = "Analyze", action = "GetPatternMetadata", target = name };
                    return true;
                case "summary":
                    resourceCall = new { module = "Analyze", action = "Summarize", target = name };
                    return true;
                case "indexes":
                    resourceCall = new { module = "Structure", action = "GetVisualIndexes", target = name };
                    return true;
                case "logic-structure":
                    resourceCall = new { module = "Structure", action = "GetLogicStructure", target = name };
                    return true;
                default:
                    return false;
            }
        }

        public static object? ConvertToolCall(JObject request)
        {
            string? method = request["method"]?.ToString();
            if (method != "tools/call") return null;

            var paramsObj = request["params"] as JObject;
            string? toolName = paramsObj?["name"]?.ToString();
            var args = paramsObj?["arguments"] as JObject;

            if (string.IsNullOrEmpty(toolName)) return null;

            foreach (var router in _routers)
            {
                var converted = router.ConvertToolCall(toolName, args);
                if (converted != null) return converted;
            }

            return null;
        }
    }
}
