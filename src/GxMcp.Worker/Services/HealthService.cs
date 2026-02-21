using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.Worker.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class HealthService
    {
        private readonly string _indexPath;

        public HealthService()
        {
            _indexPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "search_index.json");
        }

        public string GetHealthReport()
        {
            try
            {
                if (!File.Exists(_indexPath))
                    return "{\"error\": \"Search Index not found. Run genexus_bulk_index first.\"}";

                var index = SearchIndex.FromJson(File.ReadAllText(_indexPath));
                if (index == null || index.Objects.Count == 0)
                    return "{\"error\": \"Search Index is empty.\"}";

                var report = new JObject();
                report["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                report["totalObjects"] = index.Objects.Count;

                // 1. Complexity Hotspots
                var hotspots = new JArray();
                var topHotspots = index.Objects.Values
                    .Where(o => o.Complexity > 0)
                    .OrderByDescending(o => o.Complexity)
                    .Take(10);

                foreach (var o in topHotspots)
                {
                    var item = new JObject();
                    item["name"] = o.Name;
                    item["complexity"] = o.Complexity;
                    item["type"] = o.Type;
                    hotspots.Add(item);
                }
                report["complexityHotspots"] = hotspots;

                // 2. Dead Code Detection
                var entryPointTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Transaction", "WebPanel", "DataSelector", "Menu" };
                var deadCode = new JArray();
                var candidates = index.Objects.Values
                    .Where(o => (o.CalledBy == null || o.CalledBy.Count == 0) && !entryPointTypes.Contains(o.Type ?? ""))
                    .Where(o => !IsMainObject(o))
                    .OrderByDescending(o => o.Complexity)
                    .Take(20);

                foreach (var o in candidates)
                {
                    var item = new JObject();
                    item["name"] = o.Name;
                    item["type"] = o.Type;
                    item["complexity"] = o.Complexity;
                    deadCode.Add(item);
                }
                report["deadCodeCandidates"] = deadCode;

                // 3. Circular Dependencies
                var cycles = FindCircularDependencies(index);
                var cyclesArray = new JArray();
                foreach (var cycle in cycles)
                {
                    cyclesArray.Add(string.Join(" -> ", cycle));
                }
                report["circularDependencies"] = cyclesArray;

                // 4. Summary Stats
                var stats = new JObject();
                stats["avgComplexity"] = index.Objects.Values.Average(o => o.Complexity);
                stats["maxComplexity"] = index.Objects.Values.Max(o => o.Complexity);
                stats["totalCalls"] = index.Objects.Values.Sum(o => o.Calls.Count);
                stats["orphanedObjects"] = index.Objects.Values.Count(o => (o.CalledBy == null || o.CalledBy.Count == 0));
                report["summary"] = stats;

                return report.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private bool IsMainObject(SearchIndex.IndexEntry entry)
        {
            if (entry.Description != null && entry.Description.IndexOf("main", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (entry.Tags != null && entry.Tags.Any(t => t.Equals("Main", StringComparison.OrdinalIgnoreCase))) return true;
            return false;
        }

        private List<List<string>> FindCircularDependencies(SearchIndex index)
        {
            var cycles = new List<List<string>>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new List<string>();

            foreach (var node in index.Objects.Keys)
            {
                if (!visited.Contains(node))
                {
                    DFS(node, visited, stack, index, cycles);
                }
            }

            return cycles;
        }

        private void DFS(string current, HashSet<string> visited, List<string> stack, SearchIndex index, List<List<string>> cycles)
        {
            visited.Add(current);
            stack.Add(current);

            if (index.Objects.TryGetValue(current, out var entry))
            {
                foreach (var neighbor in entry.Calls)
                {
                    int indexInStack = stack.FindIndex(s => s.Equals(neighbor, StringComparison.OrdinalIgnoreCase));
                    if (indexInStack >= 0)
                    {
                        // Cycle detected
                        var cycle = stack.Skip(indexInStack).ToList();
                        cycle.Add(neighbor);
                        if (cycles.Count < 50) 
                        {
                            if (!IsDuplicateCycle(cycle, cycles))
                                cycles.Add(cycle);
                        }
                    }
                    else if (!visited.Contains(neighbor))
                    {
                        DFS(neighbor, visited, stack, index, cycles);
                    }
                }
            }

            stack.RemoveAt(stack.Count - 1);
        }

        private bool IsDuplicateCycle(List<string> newCycle, List<List<string>> cycles)
        {
            var newSet = new HashSet<string>(newCycle, StringComparer.OrdinalIgnoreCase);
            foreach (var c in cycles)
            {
                if (c.Count == newCycle.Count && newSet.SetEquals(c))
                    return true;
            }
            return false;
        }
    }
}
