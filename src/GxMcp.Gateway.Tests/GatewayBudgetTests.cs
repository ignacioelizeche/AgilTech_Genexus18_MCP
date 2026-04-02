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
    }
}
