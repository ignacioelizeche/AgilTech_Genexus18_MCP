using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Gateway.Routers
{
    public class AnalyzeRouter : IMcpModuleRouter
    {
        public string ModuleName => "Analyze";

        public object[] GetToolDefinitions()
        {
            return new object[]
            {
                new {
                    name = "genexus_inspect",
                    description = "Comprehensive object inspection. Returns metadata, variables, structure, and signature in a single call.",
                    inputSchema = new {
                        type = "object",
                        properties = new { 
                            name = new { type = "string", description = "Object name." },
                            include = new { 
                                type = "array", 
                                items = new { type = "string", @enum = new[] { "metadata", "variables", "signature", "structure" } },
                                description = "Specific parts to include (defaults to all)."
                            }
                        },
                        required = new[] { "name" }
                    }
                },
                new {
                    name = "genexus_analyze",
                    description = "Deep semantic and structural analysis using various specialized engines.",
                    inputSchema = new {
                        type = "object",
                        properties = new { 
                            name = new { type = "string", description = "Object name." },
                            mode = new { 
                                type = "string", 
                                @enum = new[] { "linter", "navigation", "hierarchy", "impact", "data_context", "ui_context", "pattern_metadata" },
                                description = "The type of analysis to perform."
                            }
                        },
                        required = new[] { "name", "mode" }
                    }
                },
                new {
                    name = "genexus_summarize",
                    description = "Creates a semantically compressed summary of a GeneXus object to save tokens.",
                    inputSchema = new {
                        type = "object",
                        properties = new { 
                            name = new { type = "string", description = "Object name." }
                        },
                        required = new[] { "name" }
                    }
                },
                new {
                    name = "genexus_inject_context",
                    description = "Automates dependency discovery. Injects SDT structures and Procedure signatures of called objects into context.",
                    inputSchema = new {
                        type = "object",
                        properties = new { 
                            name = new { type = "string", description = "Object name." },
                            recursive = new { type = "boolean", description = "Whether to discover dependencies of dependencies (default: false).", @default = false }
                        },
                        required = new[] { "name" }
                    }
                }
            };
        }

        public object ConvertToolCall(string toolName, JObject args)
        {
            string target = args?["name"]?.ToString();
            
            switch (toolName)
            {
                case "genexus_inspect":
                    // Por padrão, se não especificar, o GetConversionContext já traz quase tudo.
                    // Para manter a compatibilidade interna, usamos o module Analyze.
                    return new { module = "Analyze", action = "GetConversionContext", target = target };

                case "genexus_summarize":
                    return new { module = "Analyze", action = "Summarize", target = target };
                
                case "genexus_inject_context":
                    return new { module = "Analyze", action = "InjectContext", target = target };

                case "genexus_analyze":
                    string mode = args?["mode"]?.ToString();
                    switch (mode)
                    {
                        case "linter":
                            return new { module = "Linter", action = "Analyze", target = target };
                        case "navigation":
                            return new { module = "Analyze", action = "GetNavigation", target = target };
                        case "hierarchy":
                            return new { module = "Analyze", action = "GetHierarchy", target = target };
                        case "impact":
                            return new { module = "Analyze", action = "Analyze", target = target };
                        case "data_context":
                            return new { module = "Analyze", action = "GetDataContext", target = target };
                        case "ui_context":
                            return new { module = "UI", action = "GetUIContext", target = target };
                        case "pattern_metadata":
                            return new { module = "Analyze", action = "GetPatternMetadata", target = target };
                        default:
                            return new { module = "Analyze", action = "Analyze", target = target };
                    }
                
                // Aliases legados (para compatibilidade se algo chamar via tool call)
                case "genexus_get_signature":
                    return new { module = "Analyze", action = "GetParameters", target = target };
                case "genexus_linter":
                    return new { module = "Linter", action = "Analyze", target = target };
                case "genexus_get_navigation":
                    return new { module = "Analyze", action = "GetNavigation", target = target };
                
                default:
                    return null;
            }
        }
    }
}
