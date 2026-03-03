using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GxMcp.Worker.Helpers;
using Artech.Architecture.Common.Objects;

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

        public string InjectContext(string targetName)
        {
            var sb = new StringBuilder();
            try
            {
                var kb = _kbService.GetKB();
                if (kb == null) return "{\"error\": \"KB not opened\"}";

                var obj = _objectService.FindObject(targetName);
                if (obj == null) return "{\"error\": \"Object not found: " + targetName + "\"}";

                sb.AppendLine($"# Context for {obj.TypeDescriptor.Name} {obj.Name}");
                sb.AppendLine();

                // Self signature
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
                } catch (Exception sigEx) {
                    Logger.Error("InjectContext sig error: " + sigEx.Message);
                }

                // Direct Dependencies via GetReferences
                var depsList = new List<DependencyInfo>();
                var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                
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
                        if (type != "SDT" && type != "Procedure" && type != "Transaction" && type != "DataProvider") continue;

                        string content = null;

                        if (type == "SDT")
                        {
                            // Use the index cache snippet if available
                            var index = _objectService.GetIndex();
                            if (index != null)
                            {
                                string key = string.Format("{0}:{1}", type, tName);
                                if (index.Objects.TryGetValue(key, out var entry) && !string.IsNullOrEmpty(entry.SourceSnippet))
                                {
                                    content = entry.SourceSnippet;
                                    Logger.Info($"InjectContext: SDT {tName} resolved from cache ({content.Length} chars)");
                                }
                            }
                            // Fallback 1: StructureParser
                            if (string.IsNullOrEmpty(content))
                            {
                                string actualType = target.TypeDescriptor?.Name ?? "null";
                                Logger.Info($"InjectContext: SDT {tName} not in cache, trying StructureParser (actualType={actualType})");
                                try {
                                    content = StructureParser.SerializeToText(target);
                                    if (!string.IsNullOrEmpty(content))
                                        Logger.Info($"InjectContext: StructureParser produced {content.Length} chars for SDT {tName}");
                                    else
                                        Logger.Info($"InjectContext: StructureParser returned empty for SDT {tName}");
                                } catch (Exception spEx) {
                                    Logger.Error($"InjectContext: StructureParser failed for SDT {tName}: {spEx.Message}");
                                }
                            }
                            // Fallback 2: SDTService (does its own FindObject lookup)
                            if (string.IsNullOrEmpty(content))
                            {
                                try {
                                    var sdtSvc = new SDTService(_objectService);
                                    string sdtJson = sdtSvc.GetSDTStructure(tName);
                                    Logger.Info($"InjectContext: SDTService for {tName} returned {sdtJson?.Length ?? 0} chars");
                                    if (!string.IsNullOrEmpty(sdtJson) && !sdtJson.StartsWith("{\"error\""))
                                        content = sdtJson;
                                } catch (Exception sdtEx) {
                                    Logger.Error($"InjectContext: SDTService failed for {tName}: {sdtEx.Message}");
                                }
                            }
                            // Final fallback: just note it exists
                            if (string.IsNullOrEmpty(content))
                                content = $"SDT {tName} (structure unavailable)";
                        }
                        else
                        {
                            // Procedure / Transaction / DataProvider: extract parm rule
                            try {
                                var pResult = _objectService.GetParametersInternal(target);
                                if (!string.IsNullOrEmpty(pResult.parmRule))
                                {
                                    var paramSb = new StringBuilder();
                                    paramSb.AppendLine(pResult.parmRule);
                                    foreach (var p in pResult.parameters)
                                        paramSb.AppendLine($"  {p.Accessor} {p.Name} : {p.Type}");
                                    content = paramSb.ToString().TrimEnd();
                                }
                            } catch { }
                        }

                        if (!string.IsNullOrEmpty(content))
                        {
                            depsList.Add(new DependencyInfo { Name = tName, Type = type, Content = content });
                        }
                    }
                    catch { }

                    if (depsList.Count >= 10) break;
                }

                Logger.Info($"InjectContext: {targetName} -> {depsList.Count} deps injected from {processed.Count} unique refs");

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

        private class DependencyInfo
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public string Content { get; set; }
        }
    }
}
