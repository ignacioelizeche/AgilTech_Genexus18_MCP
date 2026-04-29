using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class IdempotencyMiddlewareTests
    {
        [Fact]
        public async Task SameKey_SecondCallReturnsCached_WithoutHittingWorker()
        {
            var calls = 0;
            var middleware = new IdempotencyMiddleware(new IdempotencyCache(15, 1000), kbPath: "kb1");

            Task<JObject> Inner(JObject req)
            {
                calls++;
                return Task.FromResult(JObject.Parse("{\"isError\":false,\"data\":{\"id\":1}}"));
            }

            var req = JObject.Parse(
                "{\"name\":\"genexus_edit\",\"arguments\":{\"name\":\"X\",\"content\":\"<x/>\",\"idempotencyKey\":\"k1\"}}");
            var r1 = await middleware.Invoke(req, Inner);
            var r2 = await middleware.Invoke(req, Inner);

            Assert.Equal(1, calls);
            Assert.True((bool)r2["meta"]!["idempotent"]!);
        }

        [Fact]
        public async Task DryRun_BypassesCache()
        {
            var calls = 0;
            var middleware = new IdempotencyMiddleware(new IdempotencyCache(15, 1000), kbPath: "kb1");

            Task<JObject> Inner(JObject req)
            {
                calls++;
                return Task.FromResult(JObject.Parse("{\"isError\":false,\"data\":{}}"));
            }

            var req = JObject.Parse(
                "{\"name\":\"genexus_edit\",\"arguments\":{\"name\":\"X\",\"content\":\"<x/>\"," +
                "\"idempotencyKey\":\"k1\",\"dryRun\":true}}");
            await middleware.Invoke(req, Inner);
            await middleware.Invoke(req, Inner);

            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task ErrorResult_NotCached()
        {
            var calls = 0;
            var middleware = new IdempotencyMiddleware(new IdempotencyCache(15, 1000), kbPath: "kb1");

            Task<JObject> Inner(JObject req)
            {
                calls++;
                return Task.FromResult(JObject.Parse("{\"isError\":true,\"error\":{\"message\":\"boom\"}}"));
            }

            var req = JObject.Parse(
                "{\"name\":\"genexus_edit\",\"arguments\":{\"name\":\"X\",\"content\":\"<x/>\",\"idempotencyKey\":\"k1\"}}");
            await middleware.Invoke(req, Inner);
            await middleware.Invoke(req, Inner);

            Assert.Equal(2, calls);
        }
    }
}
