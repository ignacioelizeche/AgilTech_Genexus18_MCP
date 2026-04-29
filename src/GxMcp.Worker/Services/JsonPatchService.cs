using System;
using System.Linq;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Applies RFC 6902 JSON-Patch ops (add, remove, replace, test) over the
    /// canonical JSON produced by ObjectJsonMapper. move/copy are deferred.
    /// </summary>
    public sealed class JsonPatchService
    {
        public string Apply(string xml, string rootName, JArray patch)
        {
            var json = ObjectJsonMapper.ToJson(xml);
            foreach (var op in patch.OfType<JObject>())
                ApplyOp(json, op);
            return ObjectJsonMapper.ToXml(json, rootName);
        }

        private static void ApplyOp(JObject root, JObject op)
        {
            string name = op["op"]?.ToString();
            if (string.IsNullOrEmpty(name))
                throw new UsageException("usage_error", "op required");

            string path = op["path"]?.ToString();
            if (string.IsNullOrEmpty(path))
                throw new UsageException("usage_error", "path required");

            switch (name)
            {
                case "replace": Replace(root, path, op["value"]); break;
                case "remove":  Remove(root, path); break;
                case "add":     Add(root, path, op["value"]); break;
                case "test":    Test(root, path, op["value"]); break;
                default:
                    throw new UsageException("usage_error", "unknown op '" + name + "'");
            }
        }

        // ── JSON Pointer (RFC 6901) walker ──────────────────────────────────

        /// <summary>
        /// Splits "/a/b/c" → ["a","b","c"], unescaping ~1→/ and ~0→~.
        /// </summary>
        private static string[] ParsePointer(string pointer)
        {
            if (pointer == "/") return new[] { "" };
            if (!pointer.StartsWith("/"))
                throw new UsageException("usage_error", "path must start with '/'");
            return pointer.Substring(1).Split('/')
                .Select(t => t.Replace("~1", "/").Replace("~0", "~"))
                .ToArray();
        }

        /// <summary>
        /// Walks to the parent token of the leaf addressed by pointer.
        /// Returns (parent, lastToken). parent is JObject or JArray.
        /// </summary>
        private static (JToken parent, string last) ResolveParent(JObject root, string pointer)
        {
            string[] tokens = ParsePointer(pointer);
            if (tokens.Length == 0)
                throw new UsageException("usage_error", "empty path");

            JToken current = root;
            for (int i = 0; i < tokens.Length - 1; i++)
            {
                current = Step(current, tokens[i], pointer);
            }
            return (current, tokens[tokens.Length - 1]);
        }

        private static JToken Step(JToken node, string token, string fullPath)
        {
            if (node is JObject obj)
            {
                if (!obj.ContainsKey(token))
                    throw new UsageException("usage_error", "path not found: " + fullPath);
                return obj[token];
            }
            if (node is JArray arr)
            {
                int idx = ParseIndex(token, arr.Count, fullPath);
                return arr[idx];
            }
            throw new UsageException("usage_error", "path not found: " + fullPath);
        }

        private static int ParseIndex(string token, int arrayCount, string fullPath)
        {
            if (!int.TryParse(token, out int idx) || idx < 0 || idx >= arrayCount)
                throw new UsageException("usage_error", "array index out of range at: " + fullPath);
            return idx;
        }

        // ── ops ─────────────────────────────────────────────────────────────

        private static void Replace(JObject root, string pointer, JToken value)
        {
            var (parent, last) = ResolveParent(root, pointer);
            if (parent is JObject obj)
            {
                if (!obj.ContainsKey(last))
                    throw new UsageException("usage_error", "replace target not found: " + pointer);
                obj[last] = value?.DeepClone();
            }
            else if (parent is JArray arr)
            {
                int idx = ParseIndex(last, arr.Count, pointer);
                arr[idx] = value?.DeepClone();
            }
            else
            {
                throw new UsageException("usage_error", "replace target not found: " + pointer);
            }
        }

        private static void Remove(JObject root, string pointer)
        {
            var (parent, last) = ResolveParent(root, pointer);
            if (parent is JObject obj)
            {
                if (!obj.Remove(last))
                    throw new UsageException("usage_error", "remove target not found: " + pointer);
            }
            else if (parent is JArray arr)
            {
                int idx = ParseIndex(last, arr.Count, pointer);
                arr.RemoveAt(idx);
            }
            else
            {
                throw new UsageException("usage_error", "remove target not found: " + pointer);
            }
        }

        private static void Add(JObject root, string pointer, JToken value)
        {
            var (parent, last) = ResolveParent(root, pointer);
            if (parent is JObject obj)
            {
                obj[last] = value?.DeepClone();
            }
            else if (parent is JArray arr)
            {
                if (last == "-")
                {
                    // RFC 6902: "-" means append
                    arr.Add(value?.DeepClone());
                }
                else
                {
                    int idx = ParseIndex(last, arr.Count + 1, pointer);
                    arr.Insert(idx, value?.DeepClone());
                }
            }
            else
            {
                throw new UsageException("usage_error", "add target not found: " + pointer);
            }
        }

        private static void Test(JObject root, string pointer, JToken expected)
        {
            var (parent, last) = ResolveParent(root, pointer);
            JToken actual;
            if (parent is JObject obj)
            {
                if (!obj.ContainsKey(last))
                    throw new UsageException("usage_error", "test target not found: " + pointer);
                actual = obj[last];
            }
            else if (parent is JArray arr)
            {
                int idx = ParseIndex(last, arr.Count, pointer);
                actual = arr[idx];
            }
            else
            {
                throw new UsageException("usage_error", "test target not found: " + pointer);
            }

            if (!JToken.DeepEquals(actual, expected))
                throw new UsageException("usage_error",
                    "test failed at " + pointer + ": expected " +
                    (expected?.ToString() ?? "null") + " got " + (actual?.ToString() ?? "null"));
        }
    }
}
