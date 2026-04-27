using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GxMcp.Worker.Helpers;
using Artech.Architecture.Common.Objects;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class InjectionService
    {
        private readonly KbService _kbService;
        private readonly ObjectService _objectService;

        public InjectionService(KbService kbService, ObjectService objectService, AnalyzeService analyzeService)
        {
            _kbService = kbService;
            _objectService = objectService;
        }

        public string InjectContext(string targetName, bool recursive = false, string typeFilter = null)
        {
            var sb = new StringBuilder();
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var depsList = new List<DependencyInfo>();

            try
            {
                var kb = _kbService.GetKB();
                if (kb == null) return McpResponse.Error("KB not opened", targetName, null, "Open a Knowledge Base before requesting dependency injection.");

                var obj = _objectService.FindObject(targetName, typeFilter);
                if (obj == null) return McpResponse.Error("Object not found", targetName, null, "The requested object is not available in the active Knowledge Base.");

                sb.AppendLine($"# Context for {obj.TypeDescriptor.Name} {obj.Name}");
                sb.AppendLine();

                // Self signature / Structure
                try {
                    var sigResult = _objectService.GetParametersInternal(obj);
                    if (!string.IsNullOrEmpty(sigResult.parmRule))
                    {
                        sb.AppendLine("## Signature");
                        sb.AppendLine("```");
                        sb.AppendLine(sigResult.parmRule);
                        sb.AppendLine("```");
                        sb.AppendLine();
                    }

                    // Elite: If target is BC, inject its structure too
                    if (obj.TypeDescriptor.Name == "Transaction") {
                        try {
                            dynamic trn = obj;
                            if (trn.IsBusinessComponent) {
                                string bcStruct = StructureParser.SerializeToText(obj);
                                if (!string.IsNullOrEmpty(bcStruct)) {
                                    sb.AppendLine("## Business Component Structure");
                                    sb.AppendLine("```");
                                    sb.AppendLine(bcStruct);
                                    sb.AppendLine("```");
                                    sb.AppendLine();
                                }
                            }
                        } catch {}
                    }
                } catch (Exception sigEx) {
                    Logger.Error("InjectContext sig/bc error: " + sigEx.Message);
                }

                // Collect dependencies
                CollectDependencies(obj, kb, processed, depsList, recursive ? 2 : 1);

                Logger.Info($"InjectContext: {targetName} -> {depsList.Count} deps injected (recursive={recursive})");

                if (depsList.Count > 0)
                {
                    sb.AppendLine("## Dependencies");
                    foreach (var dep in depsList)
                    {
                        sb.AppendLine($"### {dep.Type}: {dep.Name}");
                        sb.AppendLine("```");
                        sb.AppendLine(dep.Content);
                        sb.AppendLine("```");
                        sb.AppendLine();
                    }
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                Logger.Error("InjectContext fatal: " + ex.Message);
                sb.AppendLine($"> Fatal Error: {ex.Message}");
                return sb.Length > 0 ? sb.ToString() : "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private void CollectDependencies(KBObject obj, dynamic kb, HashSet<string> processed, List<DependencyInfo> depsList, int depth)
        {
            if (depth <= 0) return;
            if (depsList.Count >= 20) return; // Limit to avoid token bloom

            foreach (var reference in obj.GetReferences())
            {
                try
                {
                    var target = kb.DesignModel.Objects.Get(reference.To);
                    if (target == null) continue;

                    string tName = target.Name;
                    string type = target.TypeDescriptor.Name;

                    // Skip if already processed or if it's a non-useful type
                    if (processed.Contains(tName)) continue;
                    processed.Add(tName);

                    // Only include SDTs, Procedures, Transactions, DataProviders
                    bool isUseful = (type == "SDT" || type == "Procedure" || type == "Transaction" || type == "DataProvider");
                    if (!isUseful) continue;

                    string content = null;
                    string displayType = type;

                    if (type == "SDT")
                    {
                        content = GetSDTContent(target, tName);
                    }
                    else if (type == "Transaction")
                    {
                        // Check if it's a Business Component
                        try {
                            dynamic trn = target;
                            if (trn.BusinessComponent) {
                                displayType = "BusinessComponent";
                                content = StructureParser.SerializeToText(target);
                            }
                        } catch {}
                        
                        // If not BC or structure extraction failed, just get parm
                        if (string.IsNullOrEmpty(content))
                            content = GetSignatureContent(target);
                    }
                    else
                    {
                        // Procedure / DataProvider: Smart Pruning based on Complexity
                        var index = _objectService.GetIndex();
                        string key = $"{type}:{tName}";
                        bool isSmall = false;
                        if (index != null && index.Objects.TryGetValue(key, out var entry))
                        {
                            isSmall = entry.Complexity > 0 && entry.Complexity < 100;
                        }

                        if (isSmall)
                        {
                            // Inject Full Source for small utilities
                            string sourceJson = _objectService.ReadObjectSource(tName, "Source", null, null, "mcp");
                            var sObj = JObject.Parse(sourceJson);
                            content = sObj["source"]?.ToString();
                            displayType = $"{type} (Full Source)";
                        }
                        
                        // Fallback to signature if full source extraction failed or if it's large
                        if (string.IsNullOrEmpty(content))
                            content = GetSignatureContent(target);
                    }

                    if (!string.IsNullOrEmpty(content))
                    {
                        depsList.Add(new DependencyInfo { Name = tName, Type = displayType, Content = content });
                        
                        // Recurse if needed
                        if (depth > 1)
                        {
                            CollectDependencies(target, kb, processed, depsList, depth - 1);
                        }
                    }
                }
                catch { }

                if (depsList.Count >= 20) break;
            }
        }

        private string GetSDTContent(KBObject target, string tName)
        {
            string content = null;
            // Use the index cache snippet if available
            var index = _objectService.GetIndex();
            if (index != null)
            {
                string key = string.Format("SDT:{0}", tName);
                if (index.Objects.TryGetValue(key, out var entry) && !string.IsNullOrEmpty(entry.SourceSnippet))
                {
                    return entry.SourceSnippet;
                }
            }

            // Fallback 1: StructureParser
            try {
                content = StructureParser.SerializeToText(target);
            } catch { }

            // Fallback 2: SDTService
            if (string.IsNullOrEmpty(content))
            {
                try {
                    var sdtSvc = new SDTService(_objectService);
                    string sdtJson = sdtSvc.GetSDTStructure(tName);
                    if (!string.IsNullOrEmpty(sdtJson) && !sdtJson.StartsWith("{\"error\""))
                        content = sdtJson;
                } catch { }
            }

            return content ?? $"SDT {tName} (structure unavailable)";
        }

        private string GetSignatureContent(KBObject target)
        {
            try {
                var pResult = _objectService.GetParametersInternal(target);
                if (!string.IsNullOrEmpty(pResult.parmRule))
                {
                    var paramSb = new StringBuilder();
                    paramSb.AppendLine(pResult.parmRule);
                    foreach (var p in pResult.parameters)
                        paramSb.AppendLine($"  {p.Accessor} {p.Name} : {p.Type}");
                    return paramSb.ToString().TrimEnd();
                }
            } catch { }
            return null;
        }

        private class DependencyInfo
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public string Content { get; set; }
        }
    }
}
