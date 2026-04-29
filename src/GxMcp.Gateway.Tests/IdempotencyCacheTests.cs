using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class IdempotencyCacheTests
    {
        [Fact]
        public void Miss_ReturnsNull()
        {
            var cache = new IdempotencyCache(ttlMinutes: 15, capacity: 1000);
            var hit = cache.TryGet("kb1", "genexus_edit", "k1",
                payloadHash: "h1", out var cached);
            Assert.False(hit);
            Assert.Null(cached);
        }

        [Fact]
        public void Hit_SamePayloadHash_ReturnsCachedResult()
        {
            var cache = new IdempotencyCache(15, 1000);
            var result = JObject.Parse("{\"ok\":true}");
            cache.Put("kb1", "genexus_edit", "k1", "h1", result);
            var hit = cache.TryGet("kb1", "genexus_edit", "k1", "h1", out var cached);
            Assert.True(hit);
            Assert.Equal(result.ToString(), cached!.ToString());
        }

        [Fact]
        public void Hit_DifferentPayloadHash_ThrowsConflict()
        {
            var cache = new IdempotencyCache(15, 1000);
            cache.Put("kb1", "genexus_edit", "k1", "h1", JObject.Parse("{}"));
            Assert.Throws<IdempotencyConflictException>(() =>
                cache.TryGet("kb1", "genexus_edit", "k1", "h2", out _));
        }

        [Fact]
        public void DifferentKb_DoesNotCollide()
        {
            var cache = new IdempotencyCache(15, 1000);
            cache.Put("kb1", "genexus_edit", "k1", "h1", JObject.Parse("{\"a\":1}"));
            var hit = cache.TryGet("kb2", "genexus_edit", "k1", "h1", out _);
            Assert.False(hit);
        }

        [Fact]
        public void Eviction_LruDropsOldestWhenAtCapacity()
        {
            var cache = new IdempotencyCache(15, capacity: 2);
            cache.Put("kb1", "t", "k1", "h1", JObject.Parse("{}"));
            cache.Put("kb1", "t", "k2", "h2", JObject.Parse("{}"));
            cache.Put("kb1", "t", "k3", "h3", JObject.Parse("{}")); // evicts k1
            Assert.False(cache.TryGet("kb1", "t", "k1", "h1", out _));
            Assert.True(cache.TryGet("kb1", "t", "k2", "h2", out _));
            Assert.True(cache.TryGet("kb1", "t", "k3", "h3", out _));
        }

        [Fact]
        public async Task ConcurrentSameKey_SecondCallerWaitsAndGetsSameResult()
        {
            var cache = new IdempotencyCache(15, 1000);
            var firstStarted = new System.Threading.Tasks.TaskCompletionSource<bool>();
            var releaseFirst = new System.Threading.Tasks.TaskCompletionSource<bool>();

            System.Threading.Tasks.Task<JObject> First() =>
                cache.GetOrCompute("kb1", "t", "k1", "h1", async () =>
                {
                    firstStarted.SetResult(true);
                    await releaseFirst.Task;
                    return JObject.Parse("{\"answer\":42}");
                });

            var t1 = First();
            await firstStarted.Task;
            var t2 = First(); // must wait for t1, not run factory again
            releaseFirst.SetResult(true);

            var r1 = await t1;
            var r2 = await t2;
            Assert.Equal(r1.ToString(), r2.ToString());
        }
    }
}
