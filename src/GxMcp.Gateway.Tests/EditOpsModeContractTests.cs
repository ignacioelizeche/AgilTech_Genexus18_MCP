using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway.Routers;

namespace GxMcp.Gateway.Tests
{
    public class EditOpsModeContractTests
    {
        [Fact]
        public void Edit_RoutesOpsMode()
        {
            var router = new ObjectRouter();
            var args = JObject.Parse(
                "{\"name\":\"Customer\",\"mode\":\"ops\",\"ops\":[{\"op\":\"set_attribute\",\"name\":\"X\",\"type\":\"Numeric(8.0)\"}]}");
            var msg = router.ConvertToolCall("genexus_edit", args);
            Assert.NotNull(msg);
            var obj = JObject.FromObject(msg!);
            Assert.Equal("SemanticOps", obj["module"]?.ToString());
            Assert.Equal("Apply", obj["action"]?.ToString());
            Assert.Equal("Customer", obj["target"]?.ToString());
            var opsArr = obj["ops"] as JArray;
            Assert.NotNull(opsArr);
            Assert.Single(opsArr!);
        }

        [Fact]
        public void Edit_OpsMode_PropagatesPartAndDryRun()
        {
            var router = new ObjectRouter();
            var args = JObject.Parse(
                "{\"name\":\"Customer\",\"part\":\"Structure\",\"mode\":\"ops\",\"dryRun\":true," +
                "\"ops\":[{\"op\":\"add_attribute\",\"name\":\"Foo\",\"type\":\"Numeric(4.0)\"}]}");
            var msg = router.ConvertToolCall("genexus_edit", args);
            var obj = JObject.FromObject(msg!);
            Assert.Equal("Structure", obj["part"]?.ToString());
            Assert.True(obj["dryRun"]?.ToObject<bool>());
        }

        [Fact]
        public void Edit_OpsMode_TakesPrecedenceOverPatchAndFull()
        {
            var router = new ObjectRouter();
            // mode=ops should win even if patch-style fields are present
            var args = JObject.Parse(
                "{\"name\":\"Customer\",\"mode\":\"ops\",\"content\":\"ignored\"," +
                "\"ops\":[{\"op\":\"remove_attribute\",\"name\":\"X\"}]}");
            var msg = router.ConvertToolCall("genexus_edit", args);
            var obj = JObject.FromObject(msg!);
            Assert.Equal("SemanticOps", obj["module"]?.ToString());
        }
    }
}
