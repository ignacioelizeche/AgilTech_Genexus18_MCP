using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Phase 1 contract tests: ensures removed batch tools are no longer
    /// advertised, that initialize advertises them under _meta.removedTools
    /// for self-correction, and that calling them returns JSON-RPC -32601.
    /// </summary>
    public class RemovedToolsContractTests
    {
        private static string FindToolDefinitionsJson()
        {
            // tests run from src/GxMcp.Gateway.Tests/bin/<Cfg>/<tfm>; walk up to src
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                string candidate = Path.Combine(dir, "GxMcp.Gateway", "tool_definitions.json");
                if (File.Exists(candidate)) return candidate;
                candidate = Path.Combine(dir, "src", "GxMcp.Gateway", "tool_definitions.json");
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            throw new FileNotFoundException("Could not locate tool_definitions.json from test base " + AppContext.BaseDirectory);
        }

        [Fact]
        public void ToolsList_DoesNotAdvertiseRemovedBatchTools()
        {
            string path = FindToolDefinitionsJson();
            var arr = JArray.Parse(File.ReadAllText(path));
            var names = arr.Select(t => t?["name"]?.ToString()).ToList();
            Assert.DoesNotContain("genexus_batch_read", names);
            Assert.DoesNotContain("genexus_batch_edit", names);
        }

        [Fact]
        public void Initialize_AdvertisesRemovedToolsInMeta()
        {
            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "1",
                ["method"] = "initialize"
            };

            var raw = McpRouter.Handle(request);
            Assert.NotNull(raw);
            var json = JObject.FromObject(raw!);

            var meta = json["_meta"] as JObject;
            Assert.NotNull(meta);
            Assert.Equal("mcp-axi/2", meta!["schemaVersion"]?.ToString());

            var removed = meta["removedTools"] as JArray;
            Assert.NotNull(removed);
            var names = removed!.Select(e => e?["name"]?.ToString()).ToList();
            Assert.Contains("genexus_batch_read", names);
            Assert.Contains("genexus_batch_edit", names);

            foreach (var entry in removed!)
            {
                Assert.False(string.IsNullOrWhiteSpace(entry?["replacedBy"]?.ToString()));
                Assert.False(string.IsNullOrWhiteSpace(entry?["argHint"]?.ToString()));
            }
        }

        [Fact]
        public async Task CallingRemovedTool_Returns_MethodNotFound()
        {
            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "42",
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "genexus_batch_read",
                    ["arguments"] = new JObject
                    {
                        ["items"] = new JArray()
                    }
                }
            };

            var response = await Program.ProcessMcpRequest(request);
            Assert.NotNull(response);
            var error = response!["error"] as JObject;
            Assert.NotNull(error);
            Assert.Equal(-32601, (int)error!["code"]!);
            var data = error["data"] as JObject;
            Assert.NotNull(data);
            Assert.Equal("genexus_read", data!["replacedBy"]?.ToString());
            Assert.False(string.IsNullOrWhiteSpace(data["argHint"]?.ToString()));
        }
    }
}
