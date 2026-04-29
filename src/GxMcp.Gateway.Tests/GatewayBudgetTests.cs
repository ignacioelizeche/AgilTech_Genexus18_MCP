using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class GatewayBudgetTests
    {
        [Theory]
        [InlineData("genexus_edit", "Events", 180000)]
        [InlineData("genexus_edit", "Source", 180000)]
        [InlineData("genexus_import_object", "Events", 300000)]
        [InlineData("genexus_import_object", "Rules", 300000)]
        [InlineData("genexus_query", null, 60000)]
        public void GetToolTimeoutMs_ShouldApplyPartAwareTimeoutPolicy(string toolName, string? part, int expected)
        {
            JObject? args = part == null ? null : new JObject { ["part"] = part };

            int timeoutMs = Program.GetToolTimeoutMs(toolName, args);

            Assert.Equal(expected, timeoutMs);
        }

        [Theory]
        [InlineData("genexus_lifecycle")]
        [InlineData("genexus_analyze")]
        [InlineData("genexus_test")]
        public void GetToolTimeoutMs_ShouldApplyHeavyOperationBudget(string toolName)
        {
            int timeoutMs = Program.GetToolTimeoutMs(toolName, null);

            Assert.Equal(600000, timeoutMs);
        }

        [Fact]
        public void TruncateResponseIfNeeded_ShouldPreserveSearchResultsInsteadOfReturningErrorEnvelope()
        {
            var results = new JArray();
            for (int i = 0; i < 400; i++)
            {
                results.Add(new JObject
                {
                    ["guid"] = Guid.NewGuid().ToString(),
                    ["name"] = $"Object{i:D4}",
                    ["type"] = "Folder",
                    ["description"] = new string('X', 300),
                    ["parent"] = "Root Module"
                });
            }

            var payload = new JObject
            {
                ["count"] = results.Count,
                ["results"] = results
            };

            var method = typeof(Program).GetMethod(
                "TruncateResponseIfNeeded",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var truncated = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_query" });

            Assert.NotNull(truncated);
            var obj = Assert.IsType<JObject>(truncated);
            Assert.Null(obj["error"]);
            Assert.NotNull(obj["results"]);
            Assert.True(obj["results"] is JArray);
            Assert.True(((JArray)obj["results"]!).Count > 0);
            Assert.True(obj["isTruncated"]?.Value<bool>() ?? false);
            Assert.True(obj.ToString(Newtonsoft.Json.Formatting.None).Length <= 80000);
        }

        [Fact]
        public void TruncateResponseIfNeeded_ShouldDropDerivedReadMetadataBeforeTruncatingSource()
        {
            var payload = new JObject
            {
                ["name"] = "RelControleExtensaoHoras",
                ["source"] = new string('S', 26000),
                ["variables"] = new JArray(new JObject { ["name"] = "Linha", ["type"] = "Numeric(4)" }),
                ["calls"] = new JArray(new JObject { ["name"] = "SubRotina", ["parmRule"] = "parm(in:&Linha);" }),
                ["dataSchema"] = new JArray(new JObject { ["table"] = "Horas" }),
                ["patternMetadata"] = new JObject { ["pattern"] = "WorkWithPlus" }
            };

            var method = typeof(Program).GetMethod(
                "TruncateResponseIfNeeded",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var truncated = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_read" });

            Assert.NotNull(truncated);
            var obj = Assert.IsType<JObject>(truncated);
            Assert.Null(obj["variables"]);
            Assert.Null(obj["calls"]);
            Assert.Null(obj["dataSchema"]);
            Assert.Null(obj["patternMetadata"]);
            Assert.NotNull(obj["source"]);
            Assert.True(obj["isTruncated"]?.Value<bool>() ?? false);
            Assert.Contains("Gateway trimmed derived metadata", obj["message"]?.Value<string>() ?? string.Empty);
        }

        [Fact]
        public void TruncateResponseIfNeeded_ShouldPreserveLargeVisualLayoutXmlWithinVisualBudget()
        {
            var payload = new JObject
            {
                ["part"] = "Layout",
                ["contentType"] = "application/xml",
                ["source"] = new string('X', 50000)
            };

            var method = typeof(Program).GetMethod(
                "TruncateResponseIfNeeded",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var truncated = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_read" });

            Assert.NotNull(truncated);
            var obj = Assert.IsType<JObject>(truncated);
            Assert.Equal("Layout", obj["part"]?.ToString());
            Assert.Equal(50000, obj["source"]?.Value<string>()?.Length);
            Assert.Null(obj["isTruncated"]);
        }

        [Fact]
        public void TruncateResponseIfNeeded_ShouldPreservePatternInstanceXmlWithinMetadataBudget()
        {
            var payload = new JObject
            {
                ["part"] = "PatternInstance",
                ["contentType"] = "application/xml",
                ["source"] = new string('P', 50000)
            };

            var method = typeof(Program).GetMethod(
                "TruncateResponseIfNeeded",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var truncated = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_read" });

            Assert.NotNull(truncated);
            var obj = Assert.IsType<JObject>(truncated);
            Assert.Equal("PatternInstance", obj["part"]?.ToString());
            Assert.Equal(50000, obj["source"]?.Value<string>()?.Length);
            Assert.Null(obj["isTruncated"]);
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldAddMetaAndListAggregates()
        {
            var payload = new JObject
            {
                ["count"] = 3,
                ["results"] = new JArray(
                    new JObject { ["name"] = "A" },
                    new JObject { ["name"] = "B" })
            };

            var args = new JObject
            {
                ["limit"] = 2,
                ["offset"] = 0
            };

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_query", args, false });
            Assert.NotNull(normalized);

            var obj = Assert.IsType<JObject>(normalized);
            Assert.Equal("mcp-axi/2", obj["meta"]?["schemaVersion"]?.ToString());
            Assert.Equal("genexus_query", obj["meta"]?["tool"]?.ToString());
            Assert.Equal(2, obj["returned"]?.Value<int>());
            Assert.Equal(3, obj["total"]?.Value<int>());
            Assert.False(obj["empty"]?.Value<bool>() ?? true);
            Assert.True(obj["hasMore"]?.Value<bool>() ?? false);
            Assert.Equal(2, obj["nextOffset"]?.Value<int>());
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldMarkNoChangeOnSuccessfulNoChangeWrite()
        {
            var payload = new JObject
            {
                ["status"] = "Success",
                ["details"] = "No change"
            };

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_edit", null, false });
            Assert.NotNull(normalized);

            var obj = Assert.IsType<JObject>(normalized);
            Assert.True(obj["noChange"]?.Value<bool>() ?? false);
            Assert.Equal("mcp-axi/2", obj["meta"]?["schemaVersion"]?.ToString());
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldAppendTruncationHintWhenTruncated()
        {
            var payload = new JObject
            {
                ["isTruncated"] = true,
                ["source"] = "abc"
            };

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_read", null, false });
            Assert.NotNull(normalized);

            var obj = Assert.IsType<JObject>(normalized);
            Assert.True(obj["meta"]?["truncated"]?.Value<bool>() ?? false);
            var help = Assert.IsType<JArray>(obj["help"]);
            Assert.Contains(help, item => item?.ToString()?.Contains("Use limit/offset", StringComparison.OrdinalIgnoreCase) == true);
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldProjectRequestedFieldsForQuery()
        {
            var payload = new JObject
            {
                ["results"] = new JArray(
                    new JObject { ["name"] = "A", ["type"] = "Procedure", ["path"] = "M/Procs", ["description"] = "Long text" })
            };

            var args = new JObject
            {
                ["fields"] = "name,type"
            };

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_query", args, false });
            var obj = Assert.IsType<JObject>(normalized);
            var rows = Assert.IsType<JArray>(obj["results"]);
            var first = Assert.IsType<JObject>(rows[0]);

            Assert.NotNull(first["name"]);
            Assert.NotNull(first["type"]);
            Assert.Null(first["path"]);
            Assert.Equal("name", obj["meta"]?["fields"]?[0]?.ToString());
            Assert.Equal("type", obj["meta"]?["fields"]?[1]?.ToString());
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldApplyCompactDefaultsForListObjects()
        {
            var payload = new JObject
            {
                ["results"] = new JArray(
                    new JObject
                    {
                        ["name"] = "InvoiceProc",
                        ["type"] = "Procedure",
                        ["path"] = "Main/Procs/InvoiceProc",
                        ["parentPath"] = "Main/Procs",
                        ["description"] = "verbose"
                    })
            };

            var args = new JObject
            {
                ["axiCompact"] = true
            };

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_list_objects", args, false });
            var obj = Assert.IsType<JObject>(normalized);
            var first = Assert.IsType<JObject>(Assert.IsType<JArray>(obj["results"])[0]);

            Assert.NotNull(first["name"]);
            Assert.NotNull(first["type"]);
            Assert.NotNull(first["path"]);
            Assert.NotNull(first["parentPath"]);
            Assert.Null(first["description"]);
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldAddEmptyStateHelpForQuery()
        {
            var payload = new JObject
            {
                ["results"] = new JArray()
            };

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_query", null, false });
            var obj = Assert.IsType<JObject>(normalized);

            Assert.True(obj["empty"]?.Value<bool>() ?? false);
            var help = Assert.IsType<JArray>(obj["help"]);
            Assert.Contains(help, item => item?.ToString()?.Contains("No matches found", StringComparison.OrdinalIgnoreCase) == true);
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldWrapArrayPayloadIntoResultsObject()
        {
            var payload = new JArray(
                new JObject { ["name"] = "A", ["type"] = "Folder" },
                new JObject { ["name"] = "B", ["type"] = "Procedure" }
            );

            var args = new JObject
            {
                ["limit"] = 10,
                ["offset"] = 0
            };

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_list_objects", args, false });
            var obj = Assert.IsType<JObject>(normalized);

            Assert.Equal("mcp-axi/2", obj["meta"]?["schemaVersion"]?.ToString());
            Assert.Equal("genexus_list_objects", obj["meta"]?["tool"]?.ToString());
            Assert.Equal(2, obj["returned"]?.Value<int>());
            Assert.False(obj["empty"]?.Value<bool>() ?? true);
            Assert.True(obj["results"] is JArray);
        }
    }
}
