using GxMcp.Worker.Structure;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class PartAccessorAndWriteServiceTests
    {
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
