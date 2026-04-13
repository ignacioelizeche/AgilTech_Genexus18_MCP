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
        public const string ServerVersion = "1.1.7";
        public const string SupportedProtocolVersion = "2025-06-18";
        private static readonly string[] _objectParts = { "Source", "Rules", "Events", "Variables", "Structure", "Layout", "WebForm", "PatternInstance", "PatternVirtual" };
        private static readonly string[] _analysisIncludes = { "metadata", "variables", "signature", "structure" };
        private static readonly string[] _targetLanguages = { "CSharp", "TypeScript", "Java", "Python" };
        private static readonly string[] _visualSurfaces = { "Layout", "WebForm", "PatternInstance", "PatternVirtual" };
        private static readonly IReadOnlyDictionary<string, PromptDefinition> _promptDefinitions = BuildPromptDefinitions();
        private static readonly string[] _promptNames = _promptDefinitions.Keys.ToArray();
        private static readonly List<IMcpModuleRouter> _routers;
        private static JArray _toolDefinitions = new JArray();

        private sealed class PromptArgumentDefinition
        {
            public PromptArgumentDefinition(string name, string description, bool required, params string[] allowedValues)
            {
                Name = name;
                Description = description;
                Required = required;
                AllowedValues = allowedValues?.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? Array.Empty<string>();
            }

            public string Name { get; }
            public string Description { get; }
            public bool Required { get; }
            public string[] AllowedValues { get; }
        }

        private sealed class PromptDefinition
        {
            public PromptDefinition(string name, string description, Func<JObject, string> buildMessage, params PromptArgumentDefinition[] arguments)
            {
                Name = name;
                Description = description;
                BuildMessage = buildMessage;
                Arguments = arguments ?? Array.Empty<PromptArgumentDefinition>();
            }

            public string Name { get; }
            public string Description { get; }
            public PromptArgumentDefinition[] Arguments { get; }
            public Func<JObject, string> BuildMessage { get; }
        }

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
                            new { uri = "genexus://kb/agent-playbook", name = "GeneXus Agent Playbook", description = "Recommended MCP workflow to operate this GeneXus server in an agent-native, Git-friendly way." },
                            new { uri = "genexus://kb/llm-playbook", name = "LLM CLI+MCP Playbook", description = "Protocol-first guide for choosing CLI vs MCP, token-efficient calls, and timeout/lifecycle handling." },
                            new { uri = "genexus://objects", name = "GeneXus Objects Index", description = "Browsable index of all objects in the KB." },
                            new { uri = "genexus://attributes", name = "GeneXus Attributes", description = "Browsable list of all attributes." }
                        }
                    };
                case "resources/read":
                    return BuildStaticResourceResponse(request);
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
            else if (refType == "ref/prompt" && TryGetPromptArgumentDefinition(refName, argumentName, out var promptArgument))
            {
                values = promptArgument.AllowedValues;
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
                    values = new[] { "build", "rebuild", "reorg", "validate", "sync", "index", "status", "result" };
                else if (refName == "genexus_properties")
                    values = new[] { "get", "set" };
                else if (refName == "genexus_asset")
                    values = new[] { "find", "read", "write" };
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
            return _promptDefinitions.Values
                .Select(prompt => new
                {
                    name = prompt.Name,
                    description = prompt.Description,
                    arguments = prompt.Arguments.Select(argument => new
                    {
                        name = argument.Name,
                        description = argument.Description,
                        required = argument.Required,
                        allowedValues = argument.AllowedValues.Length > 0 ? argument.AllowedValues : null
                    }).ToArray()
                })
                .Cast<object>()
                .ToArray();
        }

        private static object BuildPromptResponse(JObject request)
        {
            var paramsObj = request["params"] as JObject;
            string promptName = paramsObj?["name"]?.ToString() ?? "";
            var args = paramsObj?["arguments"] as JObject ?? new JObject();
            if (!_promptDefinitions.TryGetValue(promptName, out var prompt))
            {
                return new
                {
                    description = "Unknown prompt.",
                    messages = new[]
                    {
                        CreatePromptMessage($"Prompt '{promptName}' is not defined by this server.")
                    }
                };
            }

            string? validationError = ValidatePromptArguments(prompt, args);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                return new
                {
                    description = "Invalid prompt arguments.",
                    messages = new[]
                    {
                        CreatePromptMessage(validationError)
                    }
                };
            }

            return new
            {
                description = prompt.Description,
                messages = new[]
                {
                    CreatePromptMessage(prompt.BuildMessage(args))
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

        private static string BuildAgentShipChangePrompt(string goal, string objectName, string part)
        {
            string normalizedPart = string.IsNullOrWhiteSpace(part) ? "Source" : part;
            string objectSpecificGuidance = string.IsNullOrWhiteSpace(objectName)
                ? "Start with `genexus_query` and the KB-level resources to identify the smallest object set involved before editing anything. "
                : $"Treat '{objectName}' as the primary object. Read 'genexus://objects/{objectName}/summary', 'genexus://objects/{objectName}/part/{normalizedPart}', 'genexus://objects/{objectName}/variables', and 'genexus://objects/{objectName}/hierarchy' before proposing edits. ";

            return
                $"Execute a controlled GeneXus change with the goal '{goal}'. " +
                "Start by reading 'genexus://kb/agent-playbook'. " +
                objectSpecificGuidance +
                "Use MCP discovery instead of hardcoded assumptions, keep the blast radius explicit, and prefer the smallest reversible change set. " +
                "If editing is required, re-read the exact target before mutation, persist the change, then verify with a re-read plus the appropriate lifecycle command (`validate`, `build`, or `test`). " +
                "Finish with a Git-ready change summary listing modified objects, verification evidence, and open risks.";
        }

        private static string BuildVisualChangePrompt(string name, string changeGoal, string preferredSurface)
        {
            string normalizedSurface = string.IsNullOrWhiteSpace(preferredSurface) ? "PatternInstance" : preferredSurface;
            return
                $"Plan and validate a GeneXus visual metadata change for '{name}' with the goal '{changeGoal}'. " +
                "Start by reading 'genexus://kb/agent-playbook'. " +
                $"Inspect 'genexus://objects/{name}/ui-context', 'genexus://objects/{name}/pattern-metadata', and 'genexus://objects/{name}/part/{normalizedSurface}' first. " +
                "Determine the authoritative surface before editing: base layout, raw WebForm metadata, or pattern-owned metadata. " +
                "If assets are involved, inspect `genexus_asset` metadata before changing any binary file. " +
                "After the write, re-read the exact same surface and report whether persistence is confirmed or still blocked.";
        }

        private static string BuildBootstrapLlmPrompt(string goal)
        {
            string goalHint = string.IsNullOrWhiteSpace(goal)
                ? "If the user goal is unknown, ask one concise clarifying question before editing."
                : $"User goal: '{goal}'. Prioritize next calls for this goal.";

            return
                "Bootstrap this GeneXus MCP session in protocol-first mode. " +
                "Start with discovery (`tools/list`, `resources/list`, `prompts/list`). " +
                "Read `genexus://kb/llm-playbook` and summarize: when to use AXI CLI vs MCP, pagination/field-shaping defaults, and timeout follow-up via `genexus_lifecycle(op:<operationId>)`. " +
                $"{goalHint} " +
                "Then propose the next 3 deterministic calls with explicit arguments.";
        }

        private static IReadOnlyDictionary<string, PromptDefinition> BuildPromptDefinitions()
        {
            var prompts = new[]
            {
                new PromptDefinition(
                    "gx_bootstrap_llm",
                    "Bootstrap an LLM session with protocol-first CLI+MCP usage guidance.",
                    args => BuildBootstrapLlmPrompt(args["goal"]?.ToString() ?? string.Empty),
                    new PromptArgumentDefinition("goal", "Optional current user objective to prioritize the next MCP calls.", false)),
                new PromptDefinition(
                    "gx_explain_object",
                    "Explain a GeneXus object using source, variables, navigation, and summary context.",
                    args => BuildExplainObjectPrompt(
                        args["name"]?.ToString() ?? string.Empty,
                        args["part"]?.ToString() ?? "Source"),
                    new PromptArgumentDefinition("name", "GeneXus object name.", true),
                    new PromptArgumentDefinition("part", "Primary part to emphasize during the explanation.", false, _objectParts)),
                new PromptDefinition(
                    "gx_convert_object",
                    "Prepare a GeneXus object for conversion to another language using conversion context and target-specific guidance.",
                    args => BuildConvertObjectPrompt(
                        args["name"]?.ToString() ?? string.Empty,
                        args["targetLanguage"]?.ToString() ?? "CSharp"),
                    new PromptArgumentDefinition("name", "GeneXus object name.", true),
                    new PromptArgumentDefinition("targetLanguage", "Target language for conversion.", true, _targetLanguages)),
                new PromptDefinition(
                    "gx_review_transaction",
                    "Review a Transaction object with focus on structure, rules, and generated impact.",
                    args => BuildReviewTransactionPrompt(args["name"]?.ToString() ?? string.Empty),
                    new PromptArgumentDefinition("name", "Transaction object name.", true)),
                new PromptDefinition(
                    "gx_refactor_procedure",
                    "Refactor a Procedure with attention to readability, side effects, and migration safety.",
                    args => BuildRefactorProcedurePrompt(args["name"]?.ToString() ?? string.Empty),
                    new PromptArgumentDefinition("name", "Procedure object name.", true)),
                new PromptDefinition(
                    "gx_generate_tests",
                    "Generate a test plan from source, variables, navigation, and business context.",
                    args => BuildGenerateTestsPrompt(args["name"]?.ToString() ?? string.Empty),
                    new PromptArgumentDefinition("name", "GeneXus object name.", true)),
                new PromptDefinition(
                    "gx_trace_dependencies",
                    "Trace upstream and downstream dependencies for a GeneXus object.",
                    args => BuildTraceDependenciesPrompt(args["name"]?.ToString() ?? string.Empty),
                    new PromptArgumentDefinition("name", "GeneXus object name.", true)),
                new PromptDefinition(
                    "gx_agent_ship_change",
                    "Guide an agent through a controlled GeneXus change with MCP discovery, verification, and Git-ready reporting.",
                    args => BuildAgentShipChangePrompt(
                        args["goal"]?.ToString() ?? string.Empty,
                        args["objectName"]?.ToString() ?? string.Empty,
                        args["part"]?.ToString() ?? "Source"),
                    new PromptArgumentDefinition("goal", "User-visible outcome or change objective.", true),
                    new PromptArgumentDefinition("objectName", "Primary GeneXus object when the scope is already known.", false),
                    new PromptArgumentDefinition("part", "Primary part to inspect first when an object is known.", false, _objectParts)),
                new PromptDefinition(
                    "gx_agent_visual_change",
                    "Guide an agent through a visual metadata change while resolving the authoritative GeneXus surface first.",
                    args => BuildVisualChangePrompt(
                        args["name"]?.ToString() ?? string.Empty,
                        args["changeGoal"]?.ToString() ?? string.Empty,
                        args["preferredSurface"]?.ToString() ?? "PatternInstance"),
                    new PromptArgumentDefinition("name", "GeneXus object name.", true),
                    new PromptArgumentDefinition("changeGoal", "Requested UI or metadata change.", true),
                    new PromptArgumentDefinition("preferredSurface", "Best initial guess for the authoritative editable surface.", false, _visualSurfaces))
            };

            return prompts.ToDictionary(prompt => prompt.Name, StringComparer.Ordinal);
        }

        private static string? ValidatePromptArguments(PromptDefinition prompt, JObject args)
        {
            foreach (var argument in prompt.Arguments)
            {
                string value = args[argument.Name]?.ToString() ?? string.Empty;
                if (argument.Required && string.IsNullOrWhiteSpace(value))
                {
                    return $"Missing required argument '{argument.Name}' for prompt '{prompt.Name}'.";
                }

                if (!string.IsNullOrWhiteSpace(value) &&
                    argument.AllowedValues.Length > 0 &&
                    !argument.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    return $"Invalid value '{value}' for argument '{argument.Name}' in prompt '{prompt.Name}'. Allowed values: {string.Join(", ", argument.AllowedValues)}.";
                }
            }

            return null;
        }

        private static bool TryGetPromptArgumentDefinition(string promptName, string argumentName, out PromptArgumentDefinition argument)
        {
            argument = null!;
            if (!_promptDefinitions.TryGetValue(promptName, out var prompt))
            {
                return false;
            }

            var found = prompt.Arguments.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, argumentName, StringComparison.OrdinalIgnoreCase));

            if (found == null)
            {
                return false;
            }

            argument = found;
            return true;
        }

        private static object? BuildStaticResourceResponse(JObject request)
        {
            string uri = request["params"]?["uri"]?.ToString() ?? string.Empty;
            if (string.Equals(uri, "genexus://kb/agent-playbook", StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    contents = new[]
                    {
                        new
                        {
                            uri = "genexus://kb/agent-playbook",
                            mimeType = "text/markdown",
                            text = BuildAgentPlaybook()
                        }
                    }
                };
            }

            if (string.Equals(uri, "genexus://kb/llm-playbook", StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    contents = new[]
                    {
                        new
                        {
                            uri = "genexus://kb/llm-playbook",
                            mimeType = "text/markdown",
                            text = BuildLlmCliMcpPlaybook()
                        }
                    }
                };
            }

            return null;
        }

        private static string BuildAgentPlaybook()
        {
            return
                "# GeneXus Agent Playbook\n\n" +
                "Use this server in an agent-native way:\n" +
                "1. Start with MCP discovery (`tools/list`, `resources/list`, `resources/templates/list`, `prompts/list`).\n" +
                "2. Prefer resources for read-only grounding and use tool calls only for mutations or deeper analysis.\n" +
                "3. Keep GeneXus artifacts reviewable and Git-friendly: small diffs, explicit blast radius, and post-write verification.\n" +
                "4. For code or metadata changes, re-read before editing, write once, then confirm persistence with a second read.\n" +
                "5. Close the loop with the relevant lifecycle action (`validate`, `build`, `test`, or `index`) instead of stopping at a successful write.\n" +
                "6. When the authoritative surface is unclear, inspect summary, hierarchy, ui-context, pattern metadata, and visual parts before mutating anything.\n" +
                "7. Treat assets and visual metadata as first-class artifacts: inspect metadata first, then opt into heavy content only when necessary.\n\n" +
                "Current server strengths:\n" +
                "- MCP-first gateway and discovery\n" +
                "- Source, metadata, pattern, and asset operations\n" +
                "- Prompt and completion support\n\n" +
                "Current caution points:\n" +
                "- Some visual metadata flows still require practical persistence confirmation.\n" +
                "- Extension lint warnings are legacy debt; runtime validation is stronger than stylistic cleanliness today.\n" +
                "- Prompt flows are grounded, but the agent must still choose the smallest safe change set.";
        }

        private static string BuildLlmCliMcpPlaybook()
        {
            return
                "# LLM CLI+MCP Playbook\n\n" +
                "Use this server with protocol-first rules:\n" +
                "1. Use AXI CLI for bootstrap and environment checks (`home`, `status`, `doctor --mcp-smoke`, `tools list`, `config show`).\n" +
                "2. Use MCP tools for KB operations (`genexus_query`, `genexus_list_objects`, `genexus_read`, `genexus_edit`, `genexus_lifecycle`).\n" +
                "3. For list/read operations, always set `limit`/`offset`; prefer narrow, paginated requests.\n" +
                "4. For `genexus_query` and `genexus_list_objects`, use `fields` or `axiCompact=true` to reduce tokens.\n" +
                "5. Parse MCP tool payload from `result.content[0].text` as JSON.\n" +
                "6. Expect additive metadata: `meta.schemaVersion=mcp-axi/1`, `meta.tool`, plus collection helpers (`returned`, `total`, `empty`, `hasMore`, `nextOffset`) when inferable.\n" +
                "7. If `result.isError=true` and `operationId` is present, treat as running operation and poll `genexus_lifecycle(action='status'|'result', target='op:<operationId>')`.\n" +
                "8. For safe mutation flows, use patch `dryRun` first, then apply and re-read for persistence confirmation.\n\n" +
                "Recommended bootstrap sequence:\n" +
                "- `tools/list`\n" +
                "- `resources/list`\n" +
                "- `prompts/list`\n" +
                "- `resources/read` for `genexus://kb/llm-playbook`";
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
