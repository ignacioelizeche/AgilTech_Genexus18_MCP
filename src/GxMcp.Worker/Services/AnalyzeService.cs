using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class AnalyzeService
    {
        private readonly KbService _kbService;
        private readonly ObjectService _objectService;
        private readonly IndexCacheService _indexCacheService;

        private readonly UIService _uiService;

        public AnalyzeService(KbService kbService, ObjectService objectService, IndexCacheService indexCacheService, UIService uiService = null)
        {
            _kbService = kbService;
            _objectService = objectService;
            _indexCacheService = indexCacheService;
            _uiService = uiService;
        }

        public string Analyze(string target, string typeFilter = null)
        {
            try
            {
                var kb = _kbService.GetKB();
                if (target == null) return "{\"status\":\"KB analysis not implemented for all objects yet\"}";

                var obj = _objectService.FindObject(target, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(target, _indexCacheService.GetIndex());

                var result = new JObject();
                result["name"] = obj.Name;
                result["type"] = obj.TypeDescriptor.Name;
                
                // Add dependencies (calls)
                var calls = new JArray();
                foreach (var reference in obj.GetReferences())
                {
                    var targetObj = kb.DesignModel.Objects.Get(reference.To);
                    if (targetObj != null) {
                        var cObj = new JObject();
                        cObj["name"] = targetObj.Name;
                        cObj["type"] = targetObj.TypeDescriptor.Name;
                        cObj["description"] = targetObj.Description;
                        calls.Add(cObj);
                    }
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
                if (index == null || index.Objects == null) return Models.McpResponse.Error("Index not found", targetName, null, "Run the KB indexing flow before requesting impact analysis.");

                // PERFORMANCE: Fix key lookup (Index keys are "Type:Name")
                SearchIndex.IndexEntry targetNode = null;
                string foundKey = null;

                // 1. Try exact match if targetName already contains ":"
                if (targetName.Contains(":") && index.Objects.TryGetValue(targetName, out targetNode)) {
                    foundKey = targetName;
                }
                else {
                    // 2. Search for the name across all types (Procedure:Name, Table:Name, etc)
                    var possibleKeys = index.Objects.Keys.Where(k => k.EndsWith(":" + targetName, StringComparison.OrdinalIgnoreCase)).ToList();
                    
                    if (possibleKeys.Count == 0) {
                        // FALLBACK: Try direct KB find and trigger a single-object index update
                        var obj = _objectService.FindObject(targetName);
                        if (obj != null) {
                            return "{\"target\": \"" + obj.Name + "\", \"status\": \"Indexing in progress for this object. Please retry in a few seconds.\", \"totalAffected\": 0}";
                        }
                        return Models.McpResponse.Error("Object not found in index", targetName, null, "The object was not found in the search index or the active Knowledge Base.");
                    }
                    
                    // If multiple types match, prioritize Procedures/Transactions/Tables
                    foundKey = possibleKeys.FirstOrDefault(k => k.StartsWith("Procedure:", StringComparison.OrdinalIgnoreCase))
                             ?? possibleKeys.FirstOrDefault(k => k.StartsWith("Transaction:", StringComparison.OrdinalIgnoreCase))
                             ?? possibleKeys.FirstOrDefault(k => k.StartsWith("Table:", StringComparison.OrdinalIgnoreCase))
                             ?? possibleKeys.First();
                    
                    index.Objects.TryGetValue(foundKey, out targetNode);
                }

                targetName = targetNode.Name; // Use canonical name

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
                        dynamic attr = obj;
                        var json = new JObject();
                        json["name"] = attr.Name;
                        json["description"] = attr.Description;
                        json["type"] = attr.Type.ToString();
                        json["length"] = (int)attr.Length;
                        json["decimals"] = (int)attr.Decimals;
                        
                        try {
                            if (attr.Table != null) {
                                json["table"] = attr.Table.Name;
                            }
                        } catch {}

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

        public string GetVariables(string name, string typeFilter = null)
        {
            try
            {
                var obj = _objectService.FindObject(name, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(name, _indexCacheService.GetIndex());

                dynamic vPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.GetType().Name.Equals("VariablesPart"));
                if (vPart == null) return Models.McpResponse.Error("Variables part not found", name, "Variables", "The object does not expose a Variables part.", obj.Name, obj.TypeDescriptor?.Name, new JArray(GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj)));

                var variables = new JArray();
                foreach (Variable var in vPart.Variables)
                {
                    var item = new JObject();
                    item["name"] = var.Name;
                    item["type"] = var.Type.ToString();
                    variables.Add(item);
                }
                
                var result = new JObject();
                result["variables"] = variables;
                result["source"] = VariableInjector.GetVariablesAsText((dynamic)vPart);
                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string GetHierarchy(string name, string typeFilter = null)
        {
            try
            {
                var kb = _kbService.GetKB();
                var obj = _objectService.FindObject(name, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(name, _indexCacheService.GetIndex());

                var result = new JObject();
                result["name"] = obj.Name;
                result["type"] = obj.TypeDescriptor.Name;

                // Calls (outgoing)
                var calls = new JArray();
                foreach (var reference in obj.GetReferences())
                {
                    var targetObj = kb.DesignModel.Objects.Get(reference.To);
                    if (targetObj != null) calls.Add(new JObject { 
                        ["name"] = targetObj.Name, 
                        ["type"] = targetObj.TypeDescriptor.Name,
                        ["description"] = targetObj.Description
                    });
                }
                result["calls"] = calls;

                // CalledBy (incoming)
                var calledBy = new JArray();
                foreach (var reference in obj.GetReferencesTo())
                {
                    var sourceObj = kb.DesignModel.Objects.Get(reference.From);
                    if (sourceObj != null) calledBy.Add(new JObject { 
                        ["name"] = sourceObj.Name, 
                        ["type"] = sourceObj.TypeDescriptor.Name,
                        ["description"] = sourceObj.Description
                    });
                }
                result["calledBy"] = calledBy;

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string GetConversionContext(string name, JArray include = null, string typeFilter = null)
        {
            try
            {
                var obj = _objectService.FindObject(name, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(name, _indexCacheService.GetIndex());

                var result = new JObject();
                result["name"] = obj.Name;
                result["type"] = obj.TypeDescriptor.Name;
                result["description"] = obj.Description;

                bool includeAll = (include == null || include.Count == 0);
                HashSet<string> requested = includeAll ? new HashSet<string>() : new HashSet<string>(include.Select(i => i.ToString().ToLower()));

                // PERFORMANCE: Parallel execution of metadata extraction
                var tasks = new List<Task>();

                // 1. Signature (Parameters)
                if (includeAll || requested.Contains("signature"))
                {
                    tasks.Add(Task.Run(() => {
                        try {
                            var (parmRule, parms) = _objectService.GetParametersInternal(obj);
                            lock (result) {
                                if (!string.IsNullOrEmpty(parmRule)) result["parmRule"] = parmRule;
                                var parameters = new JArray();
                                foreach (var p in parms) parameters.Add(new JObject { ["name"] = p.Name, ["accessor"] = p.Accessor, ["type"] = p.Type });
                                result["parameters"] = parameters;
                            }
                        } catch {}
                    }));
                }

                // 2. Variables
                if (includeAll || requested.Contains("variables"))
                {
                    tasks.Add(Task.Run(() => {
                        try {
                            dynamic vPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.GetType().Name.Equals("VariablesPart"));
                            if (vPart != null) {
                                var variables = new JArray();
                                foreach (Variable v in vPart.Variables) {
                                    variables.Add(new JObject { ["name"] = v.Name, ["type"] = v.Type.ToString(), ["length"] = (int)v.Length, ["decimals"] = (int)v.Decimals });
                                }
                                lock (result) result["variables"] = variables;
                            }
                        } catch {}
                    }));
                }

                // 3. Structure (Rules/Events)
                if (includeAll || requested.Contains("structure"))
                {
                    tasks.Add(Task.Run(() => {
                        try {
                            var rules = GetPartSourceByName(obj, "Rules");
                            lock (result) result["rules"] = rules;
                            
                            if (obj is Procedure || obj is WebPanel) {
                                var conditions = GetPartSourceByName(obj, "Conditions");
                                lock (result) result["conditions"] = conditions;
                            }
                            if (obj is Transaction || obj is WebPanel) {
                                var events = GetPartSourceByName(obj, "Events");
                                lock (result) result["events"] = events;
                            }
                        } catch {}
                    }));
                }

                // 4. Domains & Enums
                if (includeAll || requested.Contains("metadata") || requested.Contains("variables"))
                {
                    tasks.Add(Task.Run(() => {
                        try {
                            var domains = new JArray();
                            var processedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            dynamic vPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.GetType().Name.Equals("VariablesPart"));
                            if (vPart != null) {
                                foreach (var v in vPart.Variables) {
                                    dynamic dv = v;
                                    var domain = dv.Domain ?? (dv.Attribute != null ? dv.Attribute.Domain : null);
                                    if (domain != null && !processedDomains.Contains(domain.Name)) {
                                        processedDomains.Add(domain.Name);
                                        var dObj = new JObject { ["name"] = domain.Name };
                                        var values = new JArray();
                                        foreach (var ev in ((dynamic)domain).EnumValues) values.Add(new JObject { ["name"] = ev.Name, ["value"] = ev.Value });
                                        dObj["values"] = values;
                                        domains.Add(dObj);
                                    }
                                }
                            }
                            if (domains.Count > 0) lock (result) result["domains"] = domains;
                        } catch {}
                    }));
                }

                // Wait for all metadata tasks to complete (with timeout for safety)
                Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(5));

                // 5. UI Structure (Sync because it's usually fast or has its own internal logic)
                if (_uiService != null && (includeAll || requested.Contains("structure"))) {
                    try { result["uiStructure"] = _uiService.GetSimplifiedUIStructure(obj); } catch {}
                }

                // 6. Metadata (Sync)
                if (includeAll || requested.Contains("metadata")) result["wwpMetadata"] = GetWWPMetadata(obj);

                // 6.1 Available parts (Sync) — discovery aid
                if (includeAll || requested.Contains("parts"))
                {
                    try
                    {
                        var partsArr = new JArray();
                        var available = GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj);
                        foreach (var p in available) partsArr.Add(p);
                        result["availableParts"] = partsArr;
                    }
                    catch { }
                }

                // 7. Final Summary
                result["summary"] = GenerateSummary(obj, result);

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string GenerateSummary(KBObject obj, JObject fullResult)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"{obj.TypeDescriptor.Name} {obj.Name}: {obj.Description}. ");
                
                var parms = fullResult["parameters"] as JArray;
                if (parms != null && parms.Count > 0)
                    sb.Append($"Accepts {parms.Count} parameters. ");
                
                var vars = fullResult["variables"] as JArray;
                if (vars != null && vars.Count > 0)
                    sb.Append($"Uses {vars.Count} local variables. ");

                if (fullResult["uiStructure"] != null && fullResult["uiStructure"].Type != JTokenType.Null)
                    sb.Append("Has a user interface. ");

                if (fullResult["wwpMetadata"] != null && fullResult["wwpMetadata"].Type != JTokenType.Null)
                    sb.Append("Uses WorkWithPlus patterns. ");

                return sb.ToString().Trim();
            }
            catch { return $"{obj.TypeDescriptor.Name} {obj.Name}"; }
        }

        private JObject GetWWPMetadata(KBObject obj)
        {
            var wwp = new JObject();
            try {
                dynamic dObj = obj;
                if (dObj.Properties != null) {
                    foreach (dynamic prop in dObj.Properties) {
                        string name = prop.Name;
                        if (name == "Pattern") wwp["pattern"] = prop.Value?.ToString();
                        else if (name == "MasterPage") wwp["masterPage"] = prop.Value?.ToString();
                    }
                }

                bool isWWP = false;
                try
                {
                    if (obj.Parts.Cast<KBObjectPart>().Any(p =>
                        (p.TypeDescriptor?.Name ?? string.Empty).IndexOf("Pattern", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        isWWP = true;
                    }
                }
                catch { }
                if (!isWWP && !string.IsNullOrEmpty(obj.Description) &&
                    (obj.Description.IndexOf("WorkWithPlus", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     obj.Description.IndexOf("WWP", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    isWWP = true;
                }
                wwp["isWorkWithPlusAware"] = isWWP;
            } catch {}
            return wwp;
        }

        private string GetPartSourceByName(KBObject obj, string name)
        {
            try {
                var part = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.TypeDescriptor.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (part is ISource source) return source.Source;
            } catch {}
            return null;
        }

        public string GetSignature(string name, string typeFilter = null)
        {
            try
            {
                var obj = _objectService.FindObject(name, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(name, _indexCacheService.GetIndex());

                var (parmRule, parms) = _objectService.GetParametersInternal(obj);
                var result = new JObject();
                result["name"] = obj.Name;
                result["type"] = obj.TypeDescriptor.Name;
                result["parmRule"] = parmRule;
                
                var parmArray = new JArray();
                foreach (var p in parms) {
                    parmArray.Add(new JObject { ["name"] = p.Name, ["accessor"] = p.Accessor, ["type"] = p.Type });
                }
                result["parameters"] = parmArray;
                
                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string ExplainCode(string target, string codeSnippet)
        {
            try
            {
                // This is a placeholder for AI-powered explanation.
                // In a real scenario, this would call LLM.
                return "{\"explanation\":\"Code analysis simulation\",\"originalCode\":\"" + CommandDispatcher.EscapeJsonString(codeSnippet ?? "") + "\"}";
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }
    }
}
