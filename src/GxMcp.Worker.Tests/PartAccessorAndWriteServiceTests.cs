using GxMcp.Worker;
using GxMcp.Worker.Structure;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class PartAccessorAndWriteServiceTests
    {
        private static WriteService BuildIsolatedWriteService()
        {
            var indexCache = new IndexCacheService();
            var build = new BuildService();
            var kb = new KbService(indexCache);
            kb.SetBuildService(build);
            build.SetKbService(kb);
            indexCache.SetBuildService(build);
            var obj = new ObjectService(kb, build);
            return new WriteService(obj);
        }

        [Fact]
        public void ApplySemanticOps_RejectsMissingTarget()
        {
            var ws = BuildIsolatedWriteService();
            var req = JObject.Parse("{\"ops\":[{\"op\":\"set_attribute\",\"name\":\"X\"}]}");
            string result = ws.ApplySemanticOps(req);
            var json = JObject.Parse(result);
            Assert.True(json["isError"]?.ToObject<bool>());
            Assert.Equal("usage_error", json["error"]?["code"]?.ToString());
            Assert.Contains("target", json["error"]?["message"]?.ToString());
        }

        [Fact]
        public void ApplySemanticOps_RejectsMissingOps()
        {
            var ws = BuildIsolatedWriteService();
            var req = JObject.Parse("{\"target\":\"Customer\"}");
            string result = ws.ApplySemanticOps(req);
            var json = JObject.Parse(result);
            Assert.True(json["isError"]?.ToObject<bool>());
            Assert.Equal("usage_error", json["error"]?["code"]?.ToString());
            Assert.Contains("ops", json["error"]?["message"]?.ToString());
        }

        [Fact]
        public void ApplySemanticOps_NoKb_ReportsObjectNotFound()
        {
            var ws = BuildIsolatedWriteService();
            var req = JObject.Parse(
                "{\"target\":\"Customer\",\"part\":\"Structure\"," +
                "\"ops\":[{\"op\":\"set_attribute\",\"name\":\"X\",\"type\":\"Numeric(8.0)\"}]}");
            string result = ws.ApplySemanticOps(req);
            var json = JObject.Parse(result);
            Assert.True(json["isError"]?.ToObject<bool>());
            Assert.Equal("usage_error", json["error"]?["code"]?.ToString());
            Assert.Contains("not found", json["error"]?["message"]?.ToString());
        }

        [Theory]
        [InlineData("Events", "Events", true)]
        [InlineData("Events", "Source", false)]
        [InlineData("Events", null, false)]
        [InlineData("Source", "Events", false)]
        [InlineData("Source", "Source", true)]
        [InlineData("Source", null, true)]
        [InlineData("Code", "Events", false)]
        [InlineData("Code", "Source", true)]
        public void MatchesSourcePart_ShouldRespectRequestedPart(string requestedPartName, string sourcePartName, bool expected)
        {
            Assert.Equal(expected, PartAccessor.MatchesSourcePart(requestedPartName, sourcePartName));
        }

        [Fact]
        public void IsUnchangedSourceWrite_ShouldTreatIdenticalContentAsNoChange()
        {
            Assert.True(WritePolicy.IsUnchangedSourceWrite("Event Start\r\nEndEvent", "Event Start\r\nEndEvent"));
        }

        [Fact]
        public void IsUnchangedSourceWrite_ShouldTreatDifferentContentAsChange()
        {
            Assert.False(WritePolicy.IsUnchangedSourceWrite("Event Start\r\nEndEvent", "Event Start\r\n\tmsg('x')\r\nEndEvent"));
        }

        [Fact]
        public void IsUnchangedSourceWrite_ShouldTreatNullAsEmpty()
        {
            Assert.True(WritePolicy.IsUnchangedSourceWrite(null, string.Empty));
        }

        [Fact]
        public void IsUnchangedSourceWrite_ShouldIgnoreLineEndingAndTrailingNewlineDifferences()
        {
            Assert.True(WritePolicy.IsUnchangedSourceWrite("Event Start\r\nEndEvent\r\n", "Event Start\nEndEvent"));
        }

        [Theory]
        [InlineData("Events", "Erro", "", true)]
        [InlineData("Events", "Erro, line: 1", "", true)]
        [InlineData("Events", "Error, line: 1", "", true)]
        [InlineData("Source", "Part save failed: Erro", "Erro", true)]
        [InlineData("Code", "", "", true)]
        [InlineData("Rules", "Erro", "", false)]
        [InlineData("Events", "Validation failed", "", false)]
        [InlineData("Events", "Erro", "Detailed SDK message", false)]
        [InlineData("Events", "Erro, line: 1", "Detailed SDK message", false)]
        public void ShouldRetryWithoutPartSave_ShouldOnlyRetryGenericLogicalSourceFailures(string partName, string exceptionMessage, string diagnosticText, bool expected)
        {
            Assert.Equal(expected, WritePolicy.ShouldRetryWithoutPartSave(partName, exceptionMessage, diagnosticText));
        }

        [Fact]
        public void BuildFailureDetails_ShouldDeduplicatePrimaryAndIssueDescriptions()
        {
            var issues = new JArray
            {
                new JObject { ["description"] = "Erro" },
                new JObject { ["description"] = "Object save failed" },
                new JObject { ["description"] = "object save failed" }
            };

            string details = WritePolicy.BuildFailureDetails("Erro", issues);

            Assert.Equal("Erro | Object save failed", details);
        }
    }
}
