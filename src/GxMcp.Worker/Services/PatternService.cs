using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class PatternService
    {
        private readonly IndexCacheService _indexCache;
        private readonly ObjectService _objectService;

        public PatternService(IndexCacheService indexCache, ObjectService objectService)
        {
            _indexCache = indexCache;
            _objectService = objectService;
        }

        public string GetSample(string type)
        {
            try
            {
                var index = _indexCache.GetIndex();
                if (index == null) return "{\"error\": \"Search Index not found.\"}";

                var candidates = index.Objects.Values
                    .Where(o => IsTypeMatch(o.Type, type))
                    .Where(o => o.Complexity > 5 && o.CalledBy.Count > 2)
                    .OrderBy(o => o.Complexity)
                    .Take(5)
                    .ToList();

                if (candidates.Count == 0)
                {
                    candidates = index.Objects.Values.Where(o => IsTypeMatch(o.Type, type)).Take(1).ToList();
                }

                if (candidates.Count == 0) return "{\"error\": \"No objects of type " + type + " found.\"}";

                var best = candidates.First();
                
                string source = _objectService.GetObjectSource(best.Name);
                
                var result = new JObject();
                result["sampleName"] = best.Name;
                result["type"] = best.Type;
                result["complexity"] = best.Complexity;
                result["source"] = source ?? "// No source available";

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private bool IsTypeMatch(string actual, string expected)
        {
            if (string.IsNullOrEmpty(actual)) return false;
            return actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
