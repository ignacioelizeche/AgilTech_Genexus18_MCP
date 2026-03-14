using Newtonsoft.Json.Linq;
using Xunit;

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
        public void ConvertToolCall_ShouldMapQueryFilters()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_query","arguments":{"query":"parent:\"Root Module\" @quick","limit":5000,"typeFilter":"Folder","domainFilter":"Academic"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Search", json["module"]?.ToString());
            Assert.Equal("Query", json["action"]?.ToString());
            Assert.Equal("parent:\"Root Module\" @quick", json["target"]?.ToString());
            Assert.Equal("Folder", json["typeFilter"]?.ToString());
            Assert.Equal("Academic", json["domainFilter"]?.ToString());
            Assert.Equal(5000, json["limit"]?.Value<int>());
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
    }
}
