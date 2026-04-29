using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    public sealed class IdempotencyCache
    {
        private readonly TimeSpan _ttl;
        private readonly int _capacity;
        private readonly ConcurrentDictionary<string, KbBucket> _buckets = new ConcurrentDictionary<string, KbBucket>();
        private readonly ConcurrentDictionary<(string, string, string), SemaphoreSlim> _gates =
            new ConcurrentDictionary<(string, string, string), SemaphoreSlim>();

        public IdempotencyCache(int ttlMinutes, int capacity)
        {
            _ttl = TimeSpan.FromMinutes(ttlMinutes);
            _capacity = capacity;
        }

        public bool TryGet(string kbPath, string tool, string key,
                           string payloadHash, out JObject? cached)
        {
            cached = null;
            var bucket = _buckets.GetOrAdd(kbPath, _ => new KbBucket(_capacity, _ttl));
            return bucket.TryGet(tool, key, payloadHash, out cached);
        }

        public void Put(string kbPath, string tool, string key,
                        string payloadHash, JObject result)
        {
            var bucket = _buckets.GetOrAdd(kbPath, _ => new KbBucket(_capacity, _ttl));
            bucket.Put(tool, key, payloadHash, result);
        }

        public async Task<JObject> GetOrCompute(
            string kbPath, string tool, string key, string payloadHash,
            Func<Task<JObject>> factory)
        {
            if (TryGet(kbPath, tool, key, payloadHash, out var cached))
                return cached!;

            var gate = _gates.GetOrAdd((kbPath, tool, key), _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (TryGet(kbPath, tool, key, payloadHash, out cached))
                    return cached!;
                try
                {
                    var result = await factory().ConfigureAwait(false);
                    Put(kbPath, tool, key, payloadHash, result);
                    return result;
                }
                catch (ErrorNotCacheable ex)
                {
                    return ex.Result;
                }
            }
            finally
            {
                gate.Release();
            }
        }

        private sealed class KbBucket
        {
            private readonly int _capacity;
            private readonly TimeSpan _ttl;
            private readonly LinkedList<(string Tool, string Key)> _lru = new LinkedList<(string Tool, string Key)>();
            private readonly Dictionary<(string, string), Entry> _map = new Dictionary<(string, string), Entry>();
            private readonly object _lock = new object();

            public KbBucket(int capacity, TimeSpan ttl) { _capacity = capacity; _ttl = ttl; }

            public bool TryGet(string tool, string key, string payloadHash, out JObject? cached)
            {
                cached = null;
                lock (_lock)
                {
                    if (!_map.TryGetValue((tool, key), out var entry)) return false;
                    if (DateTime.UtcNow - entry.LastAccessedAt > _ttl)
                    {
                        _map.Remove((tool, key));
                        _lru.Remove(entry.Node);
                        return false;
                    }
                    if (entry.PayloadHash != payloadHash)
                        throw new IdempotencyConflictException(
                            $"idempotency key '{key}' reused with different payload");
                    entry.LastAccessedAt = DateTime.UtcNow;
                    _lru.Remove(entry.Node);
                    _lru.AddFirst(entry.Node);
                    cached = entry.Result;
                    return true;
                }
            }

            public void Put(string tool, string key, string payloadHash, JObject result)
            {
                lock (_lock)
                {
                    if (_map.TryGetValue((tool, key), out var existing))
                    {
                        _lru.Remove(existing.Node);
                        _map.Remove((tool, key));
                    }
                    while (_map.Count >= _capacity)
                    {
                        var oldest = _lru.Last!;
                        _lru.RemoveLast();
                        _map.Remove(oldest.Value);
                    }
                    var node = new LinkedListNode<(string, string)>((tool, key));
                    _lru.AddFirst(node);
                    _map[(tool, key)] = new Entry
                    {
                        PayloadHash = payloadHash,
                        Result = result,
                        LastAccessedAt = DateTime.UtcNow,
                        Node = node
                    };
                }
            }

            private sealed class Entry
            {
                public string PayloadHash = "";
                public JObject Result = new JObject();
                public DateTime LastAccessedAt;
                public LinkedListNode<(string, string)> Node = null!;
            }
        }
    }
}
