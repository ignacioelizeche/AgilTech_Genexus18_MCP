using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class OperationTrackerTests
    {
        [Fact]
        public void CompleteFromWorker_ShouldHandleArrayResultPayload()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            string requestId = Guid.NewGuid().ToString("N");
            string operationId = tracker.StartOperation(
                requestId,
                "genexus_list_objects",
                new JObject { ["limit"] = 20 },
                Guid.NewGuid().ToString("N"));

            var workerPayload = new JObject
            {
                ["id"] = requestId,
                ["result"] = new JArray(
                    new JObject
                    {
                        ["name"] = "ACADEMICOS",
                        ["type"] = "Folder"
                    })
            };

            tracker.CompleteFromWorker(requestId, workerPayload);
            JObject status = tracker.BuildOperationStatus(operationId);

            Assert.Equal("Completed", status["status"]?.ToString());
            Assert.False(status["timedOut"]?.Value<bool>() ?? true);
        }
    }
}

