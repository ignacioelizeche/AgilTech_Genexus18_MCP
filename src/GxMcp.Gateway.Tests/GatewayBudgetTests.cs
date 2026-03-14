using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class GatewayBudgetTests
    {
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
    }
}
