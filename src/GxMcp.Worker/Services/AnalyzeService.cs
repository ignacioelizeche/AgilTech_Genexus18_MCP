using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class AnalyzeService
    {
        private readonly KbService _kbService;
        private readonly ObjectService _objectService;
        private readonly IndexCacheService _indexCacheService;

        public AnalyzeService(KbService kbService, ObjectService objectService, IndexCacheService indexCacheService)
        {
            _kbService = kbService;
            _objectService = objectService;
            _indexCacheService = indexCacheService;
        }

        public string Analyze(string target)
        {
            try
            {
                var kb = _kbService.GetKB();
                if (target == null) return "{\"status\":\"KB analysis not implemented for all objects yet\"}";
                
                var obj = _objectService.FindObject(target);
                if (obj == null) return "{\"error\":\"Object not found\"}";

                var result = new JObject();
                result["name"] = obj.Name;
                result["type"] = obj.TypeDescriptor.Name;
                
                // Add dependencies (calls)
                var calls = new JArray();
                foreach (var reference in obj.GetReferences())
                {
                    var targetObj = kb.DesignModel.Objects.Get(reference.To);
                    if (targetObj != null) calls.Add(targetObj.Name);
                }
                result["calls"] = calls;

                // Add Impact Radius if Index is available
                try {
                    var impact = GetImpactAnalysis(obj.Name);
                    result["impactAnalysis"] = JObject.Parse(impact);
                } catch {}

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string GetImpactAnalysis(string targetName)
        {
            try
            {
                var index = _indexCacheService.GetIndex();
                if (index == null || index.Objects == null) return "{\"error\": \"Index not found. Run genexus_bulk_index first.\"}";

                if (!index.Objects.TryGetValue(targetName, out var targetNode))
                {
                    // Try case-insensitive search if exact match fails
                    var key = index.Objects.Keys.FirstOrDefault(k => string.Equals(k, targetName, StringComparison.OrdinalIgnoreCase));
                    if (key == null || !index.Objects.TryGetValue(key, out targetNode))
                         return "{\"error\": \"Object '" + targetName + "' not found in index.\"}";
                    targetName = key;
                }

                var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var queue = new Queue<string>();

                // Direct callers
                if (targetNode.CalledBy != null)
                {
                    foreach (var caller in targetNode.CalledBy)
                    {
                        if (!affected.Contains(caller))
                        {
                            affected.Add(caller);
                            queue.Enqueue(caller);
                        }
                    }
                }

                // BFS Transitive
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (index.Objects.TryGetValue(current, out var node))
                    {
                        if (node.CalledBy != null)
                        {
                            foreach (var caller in node.CalledBy)
                            {
                                if (!affected.Contains(caller))
                                {
                                    affected.Add(caller);
                                    queue.Enqueue(caller);
                                }
                            }
                        }
                    }
                }

                // Calculate Score
                int score = 0;
                var entryPoints = new List<string>();

                foreach (var name in affected)
                {
                    if (index.Objects.TryGetValue(name, out var node))
                    {
                        score += GetTypeWeight(node.Type);
                        if (IsEntryPoint(node)) entryPoints.Add(name);
                    }
                }

                var json = new JObject();
                json["target"] = targetName;
                json["totalAffected"] = affected.Count;
                json["blastRadiusScore"] = score;
                json["riskLevel"] = score > 100 ? "High" : (score > 20 ? "Medium" : "Low");
                
                var topEntryPoints = new JArray();
                foreach(var ep in entryPoints.Take(50)) topEntryPoints.Add(ep);
                json["affectedEntryPoints"] = topEntryPoints;

                var topAffected = new JArray();
                foreach(var aff in affected.Take(50)) topAffected.Add(aff);
                json["topImpacted"] = topAffected;

                return json.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"Impact Analysis failed: " + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private int GetTypeWeight(string type)
        {
            if (string.IsNullOrEmpty(type)) return 1;
            var t = type.ToLower();
            if (t.Contains("transaction")) return 10;
            if (t.Contains("webpanel")) return 5;
            if (t.Contains("procedure")) return 3;
            if (t.Contains("dataselector")) return 5;
            if (t.Contains("attribute")) return 8;
            return 1;
        }

        private bool IsEntryPoint(GxMcp.Worker.Models.SearchIndex.IndexEntry node)
        {
            if (node.Type == "Transaction" || node.Type == "WebPanel") return true;
            if (!string.IsNullOrEmpty(node.Description) && node.Description.IndexOf("main", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }


        public string GetAttributeMetadata(string name)
        {
            try
            {
                var kb = _kbService.GetKB();
                foreach (var obj in kb.DesignModel.Objects.GetByName(null, null, name))
                {
                    if (string.Equals(obj.TypeDescriptor.Name, "Attribute", StringComparison.OrdinalIgnoreCase))
                    {
                        var json = new JObject();
                        json["name"] = obj.Name;
                        json["guid"] = obj.Guid.ToString();
                        return json.ToString();
                    }
                }
                return "{\"error\":\"Attribute not found\"}";
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string GetVariables(string name)
        {
            try
            {
                var obj = _objectService.FindObject(name);
                if (obj == null) return "{\"error\":\"Object not found\"}";

                var variablesPart = obj.Parts.Get<global::Artech.Genexus.Common.Parts.VariablesPart>();
                if (variablesPart == null) return "{\"error\":\"Variables part not found\"}";

                var result = new JArray();
                foreach (global::Artech.Genexus.Common.Variable var in variablesPart.Variables)
                {
                    var item = new JObject();
                    item["name"] = var.Name;
                    item["type"] = var.Type.ToString();
                    result.Add(item);
                }
                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string GetHierarchy(string name)
        {
            try
            {
                var kb = _kbService.GetKB();
                var obj = _objectService.FindObject(name);
                if (obj == null) return "{\"error\":\"Object not found\"}";

                var result = new JObject();
                result["name"] = obj.Name;
                result["type"] = obj.TypeDescriptor.Name;

                // Calls (outgoing)
                var calls = new JArray();
                foreach (var reference in obj.GetReferences())
                {
                    var targetObj = kb.DesignModel.Objects.Get(reference.To);
                    if (targetObj != null) calls.Add(new JObject { ["name"] = targetObj.Name, ["type"] = targetObj.TypeDescriptor.Name });
                }
                result["calls"] = calls;

                // CalledBy (incoming)
                var calledBy = new JArray();
                foreach (var reference in obj.GetReferencesTo())
                {
                    var sourceObj = kb.DesignModel.Objects.Get(reference.From);
                    if (sourceObj != null) calledBy.Add(new JObject { ["name"] = sourceObj.Name, ["type"] = sourceObj.TypeDescriptor.Name });
                }
                result["calledBy"] = calledBy;

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string ListSections(string target, string partName)
        {
            return "{\"status\": \"ListSections not implemented yet\"}";
        }
    }
}
