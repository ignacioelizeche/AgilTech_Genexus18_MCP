using Newtonsoft.Json.Linq;
using Xunit;
using System;

namespace GxMcp.Gateway.Tests
{
    public class McpRouterTests
    {
        [Fact]
        public void Handle_Initialize_ShouldExposeCurrentProtocolVersion()
        {
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":"1","method":"initialize"}""");

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            Assert.Equal(McpRouter.SupportedProtocolVersion, json["protocolVersion"]?.ToString());
        }

        [Fact]
        public void Handle_PromptsList_ShouldExposeWorkflowCatalog()
        {
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":"1","method":"prompts/list"}""");

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var prompts = (JArray)json["prompts"]!;
            Assert.Contains(prompts, prompt => prompt?["name"]?.ToString() == "gx_convert_object");
            Assert.Contains(prompts, prompt => prompt?["name"]?.ToString() == "gx_trace_dependencies");
            Assert.Contains(prompts, prompt => prompt?["name"]?.ToString() == "gx_agent_ship_change");
            Assert.Contains(prompts, prompt => prompt?["name"]?.ToString() == "gx_bootstrap_llm");
        }

        [Fact]
        public void Handle_PromptsGet_ShouldBuildPromptSpecificMessage()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"prompts/get","params":{"name":"gx_convert_object","arguments":{"name":"InvoiceEntry","targetLanguage":"TypeScript"}}}"""
            );

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var firstMessage = json["messages"]![0]!;
            var text = firstMessage["content"]?["text"]?.ToString() ?? "";
            Assert.Contains("InvoiceEntry", text);
            Assert.Contains("TypeScript", text);
            Assert.Contains("conversion-context", text);
        }

        [Fact]
        public void Handle_PromptsGet_ShouldRejectMissingRequiredPromptArgument()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"prompts/get","params":{"name":"gx_agent_ship_change","arguments":{"objectName":"InvoiceEntry"}}}"""
            );

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Invalid prompt arguments.", json["description"]?.ToString());
            var text = json["messages"]![0]!["content"]?["text"]?.ToString() ?? string.Empty;
            Assert.Contains("Missing required argument 'goal'", text);
        }

        [Fact]
        public void Handle_ResourcesList_ShouldExposeAgentPlaybook()
        {
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":"1","method":"resources/list"}""");

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var resources = (JArray)json["resources"]!;
            Assert.Contains(resources, resource => resource?["uri"]?.ToString() == "genexus://kb/agent-playbook");
            Assert.Contains(resources, resource => resource?["uri"]?.ToString() == "genexus://kb/llm-playbook");
        }

        [Fact]
        public void Handle_ResourcesRead_ShouldReturnAgentPlaybookContents()
        {
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":"1","method":"resources/read","params":{"uri":"genexus://kb/agent-playbook"}}""");

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var contents = (JArray)json["contents"]!;
            var first = (JObject)contents[0]!;
            Assert.Equal("genexus://kb/agent-playbook", first["uri"]?.ToString());
            Assert.Equal("text/markdown", first["mimeType"]?.ToString());
            Assert.Contains("GeneXus Agent Playbook", first["text"]?.ToString());
            Assert.Contains("Git-friendly", first["text"]?.ToString());
        }

        [Fact]
        public void Handle_ResourcesRead_ShouldReturnLlmPlaybookContents()
        {
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":"1","method":"resources/read","params":{"uri":"genexus://kb/llm-playbook"}}""");

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var contents = (JArray)json["contents"]!;
            var first = (JObject)contents[0]!;
            Assert.Equal("genexus://kb/llm-playbook", first["uri"]?.ToString());
            Assert.Equal("text/markdown", first["mimeType"]?.ToString());
            Assert.Contains("LLM CLI+MCP Playbook", first["text"]?.ToString());
            Assert.Contains("mcp-axi/1", first["text"]?.ToString());
        }

        [Fact]
        public void Handle_PromptsGet_ShouldBuildBootstrapLlmMessage()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"prompts/get","params":{"name":"gx_bootstrap_llm","arguments":{}}}"""
            );

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var text = json["messages"]![0]!["content"]?["text"]?.ToString() ?? string.Empty;
            Assert.Contains("genexus://kb/llm-playbook", text);
            Assert.Contains("tools/list", text);
            Assert.Contains("prompts/list", text);
        }

        [Fact]
        public void Handle_PromptsGet_ShouldIncludeGoalWhenProvidedForBootstrapLlm()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"prompts/get","params":{"name":"gx_bootstrap_llm","arguments":{"goal":"Refactor invoice flow"}}}"""
            );

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var text = json["messages"]![0]!["content"]?["text"]?.ToString() ?? string.Empty;
            Assert.Contains("Refactor invoice flow", text);
        }

        [Fact]
        public void Handle_CompletionComplete_ShouldSuggestPromptNames()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"completion/complete","params":{"ref":{"type":"ref/tool","name":"prompts/get"},"argument":{"name":"prompt","value":"gx_"}}}"""
            );

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var values = (JArray)json["completion"]!["values"]!;
            Assert.Contains(values, value => value?["value"]?.ToString() == "gx_explain_object");
            Assert.Contains(values, value => value?["value"]?.ToString() == "gx_generate_tests");
        }

        [Fact]
        public void Handle_CompletionComplete_ShouldSuggestPromptArgumentAllowedValues()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"completion/complete","params":{"ref":{"type":"ref/prompt","name":"gx_convert_object"},"argument":{"name":"targetLanguage","value":"Ty"}}}"""
            );

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var values = (JArray)json["completion"]!["values"]!;
            Assert.Contains(values, value => value?["value"]?.ToString() == "TypeScript");
        }

        [Fact]
        public void Handle_ResourcesTemplatesList_ShouldExposeIndexesAndLogicStructureTemplates()
        {
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":"1","method":"resources/templates/list"}""");

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var templates = (JArray)json["resourceTemplates"]!;
            Assert.Contains(templates, template => template?["uriTemplate"]?.ToString() == "genexus://objects/{name}/indexes");
            Assert.Contains(templates, template => template?["uriTemplate"]?.ToString() == "genexus://objects/{name}/logic-structure");
        }

        [Fact]
        public void Handle_CompletionComplete_ShouldSuggestStructureActions()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"completion/complete","params":{"ref":{"type":"ref/tool","name":"genexus_structure"},"argument":{"name":"action","value":"get_"}}}"""
            );

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var values = (JArray)json["completion"]!["values"]!;
            Assert.Contains(values, value => value?["value"]?.ToString() == "get_visual");
            Assert.Contains(values, value => value?["value"]?.ToString() == "get_indexes");
            Assert.Contains(values, value => value?["value"]?.ToString() == "get_logic");
        }

        [Fact]
        public void Handle_CompletionComplete_ShouldSuggestAssetActions()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"completion/complete","params":{"ref":{"type":"ref/tool","name":"genexus_asset"},"argument":{"name":"action","value":"r"}}}"""
            );

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var values = (JArray)json["completion"]!["values"]!;
            Assert.Contains(values, value => value?["value"]?.ToString() == "read");
        }

        [Fact]
        public void Handle_CompletionComplete_ShouldSuggestLifecycleResultAction()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"completion/complete","params":{"ref":{"type":"ref/tool","name":"genexus_lifecycle"},"argument":{"name":"action","value":"res"}}}"""
            );

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var values = (JArray)json["completion"]!["values"]!;
            Assert.Contains(values, value => value?["value"]?.ToString() == "result");
        }

        [Fact]
        public void Handle_CompletionComplete_ShouldSuggestPatternParts()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"completion/complete","params":{"ref":{"type":"ref/tool","name":"genexus_read"},"argument":{"name":"part","value":"Pattern"}}}"""
            );

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var values = (JArray)json["completion"]!["values"]!;
            Assert.Contains(values, value => value?["value"]?.ToString() == "PatternInstance");
            Assert.Contains(values, value => value?["value"]?.ToString() == "PatternVirtual");
        }

        [Fact]
        public void ConvertResourceCall_ShouldMapIndexesResource()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"resources/read","params":{"uri":"genexus://objects/Customer/indexes"}}"""
            );

            var result = McpRouter.ConvertResourceCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Structure", json["module"]?.ToString());
            Assert.Equal("GetVisualIndexes", json["action"]?.ToString());
            Assert.Equal("Customer", json["target"]?.ToString());
        }

        [Fact]
        public void ConvertResourceCall_ShouldMapLogicStructureResource()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"resources/read","params":{"uri":"genexus://objects/Customer/logic-structure"}}"""
            );

            var result = McpRouter.ConvertResourceCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Structure", json["module"]?.ToString());
            Assert.Equal("GetLogicStructure", json["action"]?.ToString());
            Assert.Equal("Customer", json["target"]?.ToString());
        }

        [Fact]
        public void ConvertResourceCall_ShouldMapPatternInstancePartResource()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"resources/read","params":{"uri":"genexus://objects/ControleExtensaoHoras/part/PatternInstance"}}"""
            );

            var result = McpRouter.ConvertResourceCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Read", json["module"]?.ToString());
            Assert.Equal("ExtractSource", json["action"]?.ToString());
            Assert.Equal("ControleExtensaoHoras", json["target"]?.ToString());
            Assert.Equal("PatternInstance", json["part"]?.ToString());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapCreateObjectTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_create_object","arguments":{"type":"Procedure","name":"InvoiceHelper"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Object", json["module"]?.ToString());
            Assert.Equal("Create", json["action"]?.ToString());
            Assert.Equal("InvoiceHelper", json["target"]?.ToString());
            Assert.Equal("Procedure", json["type"]?.ToString());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapOpenKbTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_open_kb","arguments":{"path":"C:\\KBs\\SampleKB"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("KB", json["module"]?.ToString());
            Assert.Equal("Open", json["action"]?.ToString());
            Assert.Equal(@"C:\KBs\SampleKB", json["target"]?.ToString());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapExportObjectTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_export_object","arguments":{"name":"InvoiceHelper","outputPath":"exports\\InvoiceHelper.txt","part":"Rules","type":"Procedure","overwrite":true}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Object", json["module"]?.ToString());
            Assert.Equal("ExportText", json["action"]?.ToString());
            Assert.Equal("InvoiceHelper", json["target"]?.ToString());
            Assert.Equal(@"exports\InvoiceHelper.txt", json["outputPath"]?.ToString());
            Assert.Equal("Rules", json["part"]?.ToString());
            Assert.Equal("Procedure", json["type"]?.ToString());
            Assert.True(json["overwrite"]?.Value<bool>() == true);
        }

        [Fact]
        public void ConvertToolCall_ShouldMapImportObjectTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_import_object","arguments":{"name":"InvoiceHelper","inputPath":"imports\\InvoiceHelper.txt","part":"Source","type":"Procedure"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Object", json["module"]?.ToString());
            Assert.Equal("ImportText", json["action"]?.ToString());
            Assert.Equal("InvoiceHelper", json["target"]?.ToString());
            Assert.Equal(@"imports\InvoiceHelper.txt", json["inputPath"]?.ToString());
            Assert.Equal("Source", json["part"]?.ToString());
            Assert.Equal("Procedure", json["type"]?.ToString());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapPatchExpectedCountAndDryRun()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_edit","arguments":{"name":"InvoiceHelper","part":"Events","mode":"patch","operation":"Replace","context":"old","content":"new","expectedCount":2,"dryRun":true}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Patch", json["module"]?.ToString());
            Assert.Equal("Apply", json["action"]?.ToString());
            Assert.Equal("InvoiceHelper", json["target"]?.ToString());
            Assert.Equal(2, json["expectedCount"]?.Value<int>());
            Assert.True(json["dryRun"]?.Value<bool>() == true);
        }

        [Fact]
        public void ConvertToolCall_ShouldMapRefactorRenameVariableTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_refactor","arguments":{"action":"RenameVariable","objectName":"InvoiceProc","target":"&oldVar","newName":"&newVar"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Refactor", json["module"]?.ToString());
            Assert.Equal("RenameVariable", json["action"]?.ToString());
            Assert.Equal("InvoiceProc", json["target"]?.ToString());
            Assert.Contains("&oldVar", json["payload"]?.ToString());
            Assert.Contains("&newVar", json["payload"]?.ToString());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapPropertiesSetTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_properties","arguments":{"action":"set","name":"Customer","propertyName":"Description","value":"Updated"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Property", json["module"]?.ToString());
            Assert.Equal("Set", json["action"]?.ToString());
            Assert.Equal("Customer", json["target"]?.ToString());
            Assert.Equal("Description", json["propertyName"]?.ToString());
            Assert.Equal("Updated", json["value"]?.ToString());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapFormatTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_format","arguments":{"code":"for each\ncustomerid = 1\nendfor"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Formatting", json["module"]?.ToString());
            Assert.Equal("Format", json["action"]?.ToString());
            Assert.Contains("for each", json["payload"]?.ToString());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapAssetReadTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_asset","arguments":{"action":"read","path":"Web/Relatorios/RelControleExtensaoHoras.xlsx"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Asset", json["module"]?.ToString());
            Assert.Equal("Read", json["action"]?.ToString());
            Assert.Equal("Web/Relatorios/RelControleExtensaoHoras.xlsx", json["target"]?.ToString());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapQueryFilters()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_query","arguments":{"query":"parentPath:\"ModuloA/Procs\" @quick","limit":5000,"typeFilter":"Folder","domainFilter":"Academic"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Search", json["module"]?.ToString());
            Assert.Equal("Query", json["action"]?.ToString());
            Assert.Equal("parentPath:\"ModuloA/Procs\" @quick", json["target"]?.ToString());
            Assert.Equal("Folder", json["typeFilter"]?.ToString());
            Assert.Equal("Academic", json["domainFilter"]?.ToString());
            Assert.Equal(5000, json["limit"]?.Value<int>());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapListObjectsParentPath()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_list_objects","arguments":{"filter":"","limit":200,"offset":20,"parent":"Procs","parentPath":"ModuloA/Procs","typeFilter":"Procedure"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("List", json["module"]?.ToString());
            Assert.Equal("Objects", json["action"]?.ToString());
            Assert.Equal("Procs", json["parent"]?.ToString());
            Assert.Equal("ModuloA/Procs", json["parentPath"]?.ToString());
            Assert.Equal("Procedure", json["typeFilter"]?.ToString());
            Assert.Equal(200, json["limit"]?.Value<int>());
            Assert.Equal(20, json["offset"]?.Value<int>());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapStructureGetVisualTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_structure","arguments":{"action":"get_visual","name":"Customer"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Structure", json["module"]?.ToString());
            Assert.Equal("GetVisualStructure", json["action"]?.ToString());
            Assert.Equal("Customer", json["target"]?.ToString());
        }

        [Fact]
        public void ConvertToolCall_ShouldPreserveHistoryVersionId()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_history","arguments":{"action":"get_source","name":"DebugGravar","versionId":102}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("History", json["module"]?.ToString());
            Assert.Equal("get_source", json["action"]?.ToString());
            Assert.Equal("DebugGravar", json["target"]?.ToString());
            Assert.Equal(102, json["versionId"]?.Value<int>());
        }

        [Fact]
        public void GatewayProcessLease_ShouldBuildStableInstanceKey()
        {
            var config = new Configuration
            {
                Server = new ServerConfig { HttpPort = 5000 },
                GeneXus = new GeneXusConfig { InstallationPath = @"C:\GeneXus\GX18" },
                Environment = new EnvironmentConfig { KBPath = @"C:\KBs\Sample", GX_SHADOW_PATH = @"C:\KBs\Sample\.gx_mirror" }
            };

            var key = GatewayProcessLease.BuildInstanceKey(config);

            Assert.Equal("port=5000|kb=c:\\kbs\\sample|program=c:\\genexus\\gx18|shadow=c:\\kbs\\sample\\.gx_mirror", key);
        }

        [Fact]
        public void GatewayProcessLease_ShouldMarkFreshCurrentProcessLeaseAsActive()
        {
            var lease = new GatewayLeaseRecord
            {
                InstanceKey = "test",
                ProcessId = Environment.ProcessId,
                UpdatedUtc = DateTime.UtcNow
            };

            Assert.True(GatewayProcessLease.IsLeaseActive(lease));
        }

        [Fact]
        public void GatewayProcessLease_ShouldRejectStaleLease()
        {
            var lease = new GatewayLeaseRecord
            {
                InstanceKey = "test",
                ProcessId = Environment.ProcessId,
                UpdatedUtc = DateTime.UtcNow - GatewayProcessLease.LeaseStaleAfter - TimeSpan.FromSeconds(1)
            };

            Assert.False(GatewayProcessLease.IsLeaseActive(lease));
        }
    }
}
