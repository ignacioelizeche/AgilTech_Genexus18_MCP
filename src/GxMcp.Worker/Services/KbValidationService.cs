using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class KbValidationService
    {
        private readonly IndexCacheService _indexCacheService;
        private readonly ObjectService _objectService;
        private readonly PatternAnalysisService _patternAnalysisService;

        private static readonly HashSet<string> _keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "and", "or", "not", "when", "isempty", "true", "false", "null", "nullvalue",
            "like", "in", "contains", "between", "from", "to", "if", "then", "else",
            "endif", "for", "endfor", "do", "exists", "noexists", "any", "count"
        };

        public KbValidationService(IndexCacheService indexCacheService, ObjectService objectService, PatternAnalysisService patternAnalysisService)
        {
            _indexCacheService = indexCacheService;
            _objectService = objectService;
            _patternAnalysisService = patternAnalysisService;
        }

        public string ValidateConditions(int limit = 0)
        {
            try
            {
                var index = _indexCacheService.GetIndex();
                if (index == null || index.Objects.Count == 0)
                    return McpResponse.Error("Index empty", null, null, "Run genexus_lifecycle(action='index') first.");

                var attrNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in index.Objects.Values)
                {
                    if (string.Equals(entry.Type, "Attribute", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entry.Name))
                        attrNames.Add(entry.Name);
                }

                var candidates = index.Objects.Values
                    .Where(e => string.Equals(e.Type, "Transaction", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(e.Type, "WebPanel", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var issues = new JArray();
                int scanned = 0;
                int patternsFound = 0;

                foreach (var entry in candidates)
                {
                    if (limit > 0 && scanned >= limit) break;
                    scanned++;

                    KBObjectPartShim shim;
                    string xml;
                    try
                    {
                        var obj = _objectService.FindObject(entry.Name);
                        if (obj == null) continue;
                        xml = _patternAnalysisService.ReadPatternPartXml(obj, "PatternInstance", out _, out _);
                    }
                    catch { continue; }

                    if (string.IsNullOrWhiteSpace(xml)) continue;
                    patternsFound++;

                    XDocument doc;
                    try { doc = XDocument.Parse(xml); }
                    catch { continue; }

                    foreach (var ga in doc.Descendants("gridAttribute"))
                    {
                        var conditions = ga.Attribute("conditions")?.Value;
                        if (string.IsNullOrWhiteSpace(conditions)) continue;

                        var attribAttr = ga.Attribute("attribute")?.Value ?? string.Empty;
                        var dash = attribAttr.LastIndexOf('-');
                        var controlName = dash >= 0 ? attribAttr.Substring(dash + 1) : attribAttr;

                        var missing = ExtractMissingAttributes(conditions, attrNames);
                        if (missing.Count > 0)
                        {
                            foreach (var m in missing)
                            {
                                issues.Add(new JObject
                                {
                                    ["object"] = entry.Name,
                                    ["objectType"] = entry.Type,
                                    ["control"] = controlName,
                                    ["conditions"] = conditions,
                                    ["missingAttribute"] = m,
                                    ["suggestion"] = "Attribute '" + m + "' not found in KB. Verify spelling or rename in PatternInstance."
                                });
                            }
                        }
                    }
                }

                var result = new JObject
                {
                    ["status"] = issues.Count == 0 ? "Ok" : "IssuesFound",
                    ["scannedObjects"] = scanned,
                    ["patternInstancesInspected"] = patternsFound,
                    ["issuesCount"] = issues.Count,
                    ["issues"] = issues
                };
                return result.ToString();
            }
            catch (Exception ex)
            {
                return McpResponse.Error("ValidateConditions failed", null, null, ex.Message);
            }
        }

        public string ListPatternSnapshots(string target)
        {
            try
            {
                if (string.IsNullOrEmpty(target))
                    return McpResponse.Error("target required", target, null, "Provide object name to list snapshots.");
                var obj = _objectService.FindObject(target);
                if (obj == null) return McpResponse.Error("Object not found", target, null, "Use type=<...> to disambiguate.");

                var files = PatternSnapshotStore.List(obj.Guid.ToString());
                var arr = new JArray();
                foreach (var f in files) arr.Add(new JObject
                {
                    ["path"] = f,
                    ["fileName"] = System.IO.Path.GetFileName(f),
                    ["sizeBytes"] = new System.IO.FileInfo(f).Length
                });
                return new JObject { ["count"] = arr.Count, ["target"] = obj.Name, ["snapshots"] = arr }.ToString();
            }
            catch (Exception ex) { return McpResponse.Error("ListPatternSnapshots failed", target, null, ex.Message); }
        }

        public string RestorePatternSnapshot(string target, string snapshotPath, WriteService writeService)
        {
            try
            {
                if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(snapshotPath))
                    return McpResponse.Error("target and snapshotPath required", target, "PatternInstance", "Use snapshots-list to find available paths.");

                var xml = PatternSnapshotStore.ReadSnapshot(snapshotPath);
                if (string.IsNullOrEmpty(xml))
                    return McpResponse.Error("Snapshot read failed", target, "PatternInstance", "File missing or unreadable: " + snapshotPath);

                return writeService.WriteObject(target, "PatternInstance", xml);
            }
            catch (Exception ex) { return McpResponse.Error("RestorePatternSnapshot failed", target, "PatternInstance", ex.Message); }
        }

        private List<string> ExtractMissingAttributes(string expression, HashSet<string> known)
        {
            var missing = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(expression, @"\b[A-Za-z_][A-Za-z0-9_]*\b"))
            {
                var token = m.Value;
                if (_keywords.Contains(token)) continue;
                if (token.Length <= 1) continue;
                if (seen.Contains(token)) continue;
                seen.Add(token);
                if (!known.Contains(token)) missing.Add(token);
            }
            return missing;
        }

        private class KBObjectPartShim { }
    }
}
