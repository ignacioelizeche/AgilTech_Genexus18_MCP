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
                    return new 
                    {
                        protocolVersion = "2025-03-26",
                        capabilities = new 
                        {
                            tools = new { listChanged = true }
                        },
                        serverInfo = new 
                        {
                            name = "genexus-mcp-server",
                            version = "3.0.0"
                        }
                    };

                case "tools/list":
                    return new { tools = GetToolDefinitions() };

                case "notifications/initialized":
                    return null;

                case "ping":
                    return new { };

                default:
                    return null;
            }
        }

        private static object[] GetToolDefinitions()
        {
            return new object[]
            {
                // === Tier 1: Core Tools ===

                new {
                    name = "genexus_build",
                    description = "Executes GeneXus Build, Sync, or Reorg operations.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            action = new { type = "string", description = "Action to perform: Build, Sync, Reorg, UpdateFromServer" },
                            target = new { type = "string", description = "Target object or 'All'" }
                        },
                        required = new[] { "action" }
                    }
                },

                new {
                    name = "genexus_read_object",
                    description = "Reads a GeneXus object structure (export to XML/XPZ).",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Name of the object to read (e.g. 'Prc:MyProc', 'Trn:MyTrn')" }
                        },
                        required = new[] { "name" }
                    }
                },

                new {
                    name = "genexus_write_object",
                    description = "Writes/modifies a GeneXus object's code (Source, Rules, or Events part).",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Name of the object to modify (e.g. 'Prc:MyProc')" },
                            part = new { type = "string", description = "Part to modify: Source, Rules, or Events" },
                            code = new { type = "string", description = "The new code content for the specified part" }
                        },
                        required = new[] { "name", "part", "code" }
                    }
                },

                new {
                    name = "genexus_list_objects",
                    description = "Lists all objects in the GeneXus Knowledge Base, optionally filtered by type.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            filter = new { type = "string", description = "Comma-separated type filter (e.g. 'Procedure,Transaction,WebPanel'). Default: all types." },
                            limit = new { type = "integer", description = "Max number of objects to return. Default: 100." },
                            offset = new { type = "integer", description = "Number of objects to skip. Default: 0." }
                        }
                    }
                },

                new {
                    name = "genexus_analyze",
                    description = "Deep analysis of a GeneXus object: extracts dependencies (calls, tables), semantic tags, and code quality insights.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Object name to analyze (e.g. 'Prc:MyProc'). If omitted, analyzes the entire KB." }
                        }
                    }
                },

                new {
                    name = "genexus_create_object",
                    description = "Creates a new GeneXus object (Transaction with attributes and structure) in the Knowledge Base.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Name of the new object" },
                            definition = new { type = "string", description = "JSON definition with Attributes array and Structure string" }
                        },
                        required = new[] { "name", "definition" }
                    }
                },

                new {
                    name = "genexus_refactor",
                    description = "Refactors a GeneXus object by cleaning unused variables or applying code improvements.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Object name to refactor (e.g. 'Prc:MyProc')" },
                            action = new { type = "string", description = "Refactoring action: CleanVars (removes unused variables)" }
                        },
                        required = new[] { "name", "action" }
                    }
                },

                new {
                    name = "genexus_doctor",
                    description = "Analyzes GeneXus build logs to diagnose errors and prescribe fixes.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            logPath = new { type = "string", description = "Path to the build log file. If omitted, uses the default msbuild.log." }
                        }
                    }
                },

                // === Tier 2: Utility Tools ===

                new {
                    name = "genexus_search",
                    description = "Searches across cached GeneXus KB objects for code matching a query (RAG-lite context retrieval).",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            query = new { type = "string", description = "Search terms to find in object source code" }
                        },
                        required = new[] { "query" }
                    }
                },

                new {
                    name = "genexus_history",
                    description = "Manages local code snapshots for undo/restore operations on GeneXus objects.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Object name (e.g. 'Prc:MyProc')" },
                            action = new { type = "string", description = "Action: Save (snapshot current code) or Restore (undo to last snapshot)" }
                        },
                        required = new[] { "name", "action" }
                    }
                },

                new {
                    name = "genexus_wiki",
                    description = "Auto-generates markdown documentation for a GeneXus object including type, description, dependencies, and source comments.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Object name to document (e.g. 'Prc:MyProc')" }
                        },
                        required = new[] { "name" }
                    }
                },

                new {
                    name = "genexus_batch",
                    description = "Atomic batch operations: buffer multiple object changes and commit them all at once to the Knowledge Base.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Object name (required for Add action)" },
                            action = new { type = "string", description = "Action: Add (buffer a change) or Commit (apply all buffered changes)" },
                            code = new { type = "string", description = "New source code (required for Add action)" }
                        },
                        required = new[] { "action" }
                    }
                },

                new {
                    name = "genexus_visualize",
                    description = "Generates an interactive HTML graph of the Knowledge Base dependencies.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            domain = new { type = "string", description = "Optional: Filter by business domain like 'Financeiro' or 'Protocolo'" },
                            type = new { type = "string", description = "Optional: Comma-separated type filter (e.g. 'Trn,Prc')" },
                            prefix = new { type = "string", description = "Optional: Filter by name prefix (e.g. 'Ptc')" },
                            name = new { type = "string", description = "Optional: Filter by exact or partial name" }
                        }
                    }
                },

                // === Tier 3: Granular Access Tools ===

                new {
                    name = "genexus_read_source",
                    description = "Returns ONLY the plain text source of a specific part, significantly reducing token usage.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Object name (e.g. 'Prc:MyProc')" },
                            part = new { type = "string", description = "Part to read: Source, Rules, or Events. Default: Source" }
                        },
                        required = new[] { "name" }
                    }
                },

                new {
                    name = "genexus_list_sections",
                    description = "Lists all Subroutines and Events within an object part.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Object name" },
                            part = new { type = "string", description = "Part to scan: Source, Rules, or Events" }
                        },
                        required = new[] { "name" }
                    }
                },

                new {
                    name = "genexus_read_section",
                    description = "Reads a specific Subroutine or Event block from an object.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Object name" },
                            part = new { type = "string", description = "Part to read from" },
                            section = new { type = "string", description = "Name of the Subroutine (e.g. 'ProcessData') or Event (e.g. 'Start')" }
                        },
                        required = new[] { "name", "section" }
                    }
                },

                new {
                    name = "genexus_write_section",
                    description = "Surgically updates a specific Subroutine or Event within an object, avoiding full source rewrites.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Object name" },
                            part = new { type = "string", description = "Part to modify" },
                            section = new { type = "string", description = "Name of the selection to replace" },
                            code = new { type = "string", description = "The new code for this specific section" }
                        },
                        required = new[] { "name", "section", "code" }
                    }
                },

                new {
                    name = "genexus_get_variables",
                    description = "Returns the list of variables defined in an object with their metadata (type, length, etc.).",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Object name (e.g. 'Prc:MyProc')" }
                        },
                        required = new[] { "name" }
                    }
                },

                new {
                    name = "genexus_get_attribute",
                    description = "Returns metadata for a specific attribute (Type, Length, Table).",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Attribute name" }
                        },
                        required = new[] { "name" }
                    }
                },

                new {
                    name = "genexus_get_hierarchy",
                    description = "Returns the level structure and tables of a Transaction.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            name = new { type = "string", description = "Transaction name (e.g. 'Trn:Customer')" }
                        },
                        required = new[] { "name" }
                    }
                },

                new {
                    name = "genexus_bulk_index",
                    description = "Performs a full KB metadata indexing for fast discovery.",
                    inputSchema = new {
                        type = "object",
                        properties = new { }
                    }
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
                case "genexus_bulk_index":
                    return new {
                        module = "genexus",
                        action = "BulkIndex"
                    };

                case "genexus_build":
                    return new {
                        module = "Build",
                        action = args?["action"]?.ToString(),
                        target = args?["target"]?.ToString() ?? "All"
                    };

                case "genexus_read_object":
                    return new {
                        module = "Read",
                        action = "Export",
                        target = args?["name"]?.ToString()
                    };

                case "genexus_write_object":
                    var writeCmd = new {
                        module = "Write",
                        action = args?["part"]?.ToString() ?? "Source",
                        target = args?["name"]?.ToString(),
                        payload = args?["code"]?.ToString()
                    };
                    Console.Error.WriteLine($"[Gateway] Converted write command: part={writeCmd.action} target={writeCmd.target}");
                    return writeCmd;

                case "genexus_list_objects":
                    return new {
                        module = "ListObjects",
                        action = "List",
                        target = args?["filter"]?.ToString() ?? "Procedure,Transaction,WebPanel",
                        limit = args?["limit"]?.ToObject<int?>() ?? 100,
                        offset = args?["offset"]?.ToObject<int?>() ?? 0
                    };

                case "genexus_analyze":
                    return new {
                        module = "Analyze",
                        action = "Deep",
                        target = args?["name"]?.ToString() ?? ""
                    };

                case "genexus_create_object":
                    return new {
                        module = "Forge",
                        action = "Create",
                        target = args?["name"]?.ToString(),
                        payload = args?["definition"]?.ToString()
                    };

                case "genexus_refactor":
                    return new {
                        module = "Refactor",
                        action = args?["action"]?.ToString() ?? "CleanVars",
                        target = args?["name"]?.ToString()
                    };

                case "genexus_doctor":
                    return new {
                        module = "Doctor",
                        action = "Diagnose",
                        target = args?["logPath"]?.ToString() ?? ""
                    };

                case "genexus_search":
                    return new {
                        module = "Search",
                        action = "Query",
                        target = args?["query"]?.ToString()
                    };

                case "genexus_history":
                    return new {
                        module = "History",
                        action = args?["action"]?.ToString(),
                        target = args?["name"]?.ToString()
                    };

                case "genexus_wiki":
                    return new {
                        module = "Wiki",
                        action = "Generate",
                        target = args?["name"]?.ToString()
                    };

                case "genexus_batch":
                    return new {
                        module = "Batch",
                        action = args?["action"]?.ToString(),
                        target = args?["name"]?.ToString() ?? "",
                        payload = args?["code"]?.ToString() ?? ""
                    };

                case "genexus_visualize":
                    var vizArgs = new JObject();
                    vizArgs["domain"] = args?["domain"]?.ToString() ?? "All";
                    vizArgs["type"] = args?["type"]?.ToString();
                    vizArgs["prefix"] = args?["prefix"]?.ToString();
                    vizArgs["name"] = args?["name"]?.ToString();

                    return new {
                        module = "Visualize",
                        action = "Generate",
                        target = vizArgs["domain"].ToString(),
                        payload = vizArgs.ToString(Formatting.None)
                    };

                case "genexus_read_source":
                    return new {
                        module = "Read",
                        action = "ExtractSource",
                        target = args?["name"]?.ToString(),
                        part = args?["part"]?.ToString() ?? "Source"
                    };

                case "genexus_list_sections":
                    return new {
                        module = "Analyze",
                        action = "ListSections",
                        target = args?["name"]?.ToString(),
                        part = args?["part"]?.ToString() ?? "Source"
                    };

                case "genexus_read_section":
                    return new {
                        module = "Read",
                        action = "ReadSection",
                        target = args?["name"]?.ToString(),
                        part = args?["part"]?.ToString() ?? "Source",
                        payload = args?["section"]?.ToString()
                    };

                case "genexus_write_section":
                    return new {
                        module = "Write",
                        action = "WriteSection",
                        target = args?["name"]?.ToString(),
                        part = args?["part"]?.ToString() ?? "Source",
                        payload = args?["code"]?.ToString(),
                        section = args?["section"]?.ToString()
                    };

                case "genexus_get_variables":
                    return new {
                        module = "Read",
                        action = "GetVariables",
                        target = args?["name"]?.ToString()
                    };

                case "genexus_get_attribute":
                    return new {
                        module = "Read",
                        action = "GetAttribute",
                        target = args?["name"]?.ToString()
                    };

                case "genexus_get_hierarchy":
                    return new {
                        module = "Analyze",
                        action = "GetHierarchy",
                        target = args?["name"]?.ToString()
                    };

                default:
                    return null;
            }
        }
    }
}
