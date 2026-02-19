using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Parts;
using Artech.Genexus.Common.Objects;
using Artech.Architecture.Common.Collections;
using GxMcp.Worker.Models;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class AnalyzeService
    {
        private readonly ObjectService _objectService;
        private readonly IndexCacheService _indexCacheService;

        private static readonly Regex CommentBlockRegex = new Regex(@"(?s)/\*.*?\*/", RegexOptions.Compiled);
        private static readonly Regex InlineCommentRegex = new Regex(@"//.*", RegexOptions.Compiled);
        private static readonly Regex CallRegex = new Regex(@"(?i)(?:call|u\s*|udp)\s*\(\s*'?([\w:]+)'?|([\w]+)\s*\.\s*call\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ForEachRegex = new Regex(@"(?i)(?:for each|new|delete)\s+([\w]+)", RegexOptions.Compiled);
        private static readonly Regex CommitInLoopRegex = new Regex(@"\bFor\s+each\b.*?Commit\b.*?EndFor\b", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex DynamicCallRegex = new Regex(@"\bCall\s*\(\s*&", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FullScanRegex = new Regex(@"\bFor\s+each\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex WhereClauseRegex = new Regex(@"\bwhere\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        
        private static readonly Dictionary<string, Regex> TagPatterns = new Dictionary<string, Regex>
        {
            {"Integration", new Regex(@"(?i)(httpclient|rest|json|tostring|fromjson|soap|location)", RegexOptions.Compiled)},
            {"Reporting", new Regex(@"(?i)(print|output_file|pdf|report)", RegexOptions.Compiled)},
            {"Heavy-Batch", new Regex(@"(?i)(for each|commit|rollback|submit)", RegexOptions.Compiled)},
            {"Security", new Regex(@"(?i)(gam|permission|encrypt|decrypt|login)", RegexOptions.Compiled)},
            {"Interface", new Regex(@"(?i)(webpanel|form\.|control\.|event\s+'|onclick)", RegexOptions.Compiled)}
        };

        public AnalyzeService(ObjectService objectService, IndexCacheService indexCacheService)
        {
            _objectService = objectService;
            _indexCacheService = indexCacheService;
            Console.Error.WriteLine($"[AnalyzeService] Initialized with IndexCacheService.");
        }

        public string Analyze(string target)
        {
            try
            {
                var result = AnalyzeInternal(target);
                if (result == null) return "{\"error\": \"Analysis failed for: " + CommandDispatcher.EscapeJsonString(target) + "\"}";

                // Build optimized JSON response
                var jsonParts = new List<string>();
                jsonParts.Add($"\"name\":\"{CommandDispatcher.EscapeJsonString(target)}\"");
                
                if (result.Calls.Count > 0)
                    jsonParts.Add($"\"calls\":[" + string.Join(",", result.Calls.Select(c => "\"" + CommandDispatcher.EscapeJsonString(c) + "\"")) + "]");
                
                if (result.Tables.Count > 0)
                    jsonParts.Add($"\"tables\":[" + string.Join(",", result.Tables.Select(t => "\"" + CommandDispatcher.EscapeJsonString(t) + "\"")) + "]");
                
                if (result.Tags.Count > 0)
                    jsonParts.Add($"\"tags\":[" + string.Join(",", result.Tags.Select(t => "\"" + t + "\"")) + "]");
                
                if (result.Rules.Count > 0)
                    jsonParts.Add($"\"rules\":[" + string.Join(",", result.Rules.Select(r => "\"" + CommandDispatcher.EscapeJsonString(r) + "\"")) + "]");
                
                if (!string.IsNullOrEmpty(result.Domain) && result.Domain != "Geral")
                    jsonParts.Add($"\"domain\":\"{result.Domain}\"");

                if (result.Insights.Count > 0)
                    jsonParts.Add($"\"insights\":[" + string.Join(",", result.Insights.Select(i => "{\"level\":\"" + i.Level + "\",\"message\":\"" + CommandDispatcher.EscapeJsonString(i.Message) + "\"}")) + "]");

                if (result.Messages.Count > 0)
                    jsonParts.Add($"\"messages\":[" + string.Join(",", result.Messages.Select(m => "\"" + CommandDispatcher.EscapeJsonString(m) + "\"")) + "]");

                jsonParts.Add($"\"complexity\":{result.Complexity}");
                jsonParts.Add($"\"codeLength\":{result.CodeLength}");

                return "{" + string.Join(",", jsonParts) + "}";
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AnalyzeService Error] {ex.Message}");
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string ListSections(string target, string partName)
        {
            try
            {
                // Delegate to ObjectService which now handles GUIDs and safe parsing
                string jsonResponse = _objectService.ReadObjectSource(target, partName);
                if (jsonResponse.Contains("\"error\"")) return jsonResponse;

                var jObj = JObject.Parse(jsonResponse);
                string partCode = jObj["source"]?.ToString() ?? "";

                var sections = CodeParser.GetSections(partCode);
                return Newtonsoft.Json.JsonConvert.SerializeObject(new { 
                    name = target, 
                    part = partName, 
                    sections = sections 
                });
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string GetTransactionHierarchy(string target)
        {
            try
            {
                var obj = _objectService.FindObject(target);
                if (obj == null) return "{\"error\":\"Object not found: " + target + "\"}";

                Transaction trn = obj as Transaction;
                if (trn == null) return "{\"error\":\"Object is not a Transaction: " + target + "\"}";

                var levels = new List<object>();
                foreach (TransactionLevel level in trn.Structure.Root.Levels)
                {
                    levels.Add(ParseLevel(level));
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new { 
                    name = target, 
                    rootTable = trn.Structure.Root.AssociatedTable?.Name,
                    levels = levels 
                });
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private object ParseLevel(TransactionLevel level)
        {
            var lObj = new JObject();
            lObj["name"] = level.Name;
            lObj["table"] = level.AssociatedTable?.Name;
            
            var attributes = new List<string>();
            foreach (TransactionAttribute att in level.Attributes)
            {
                attributes.Add(att.Name + (att.IsKey ? " (PK)" : ""));
            }
            lObj["attributes"] = new JArray(attributes);

            if (level.Levels.Count > 0)
            {
                var subLevels = new List<object>();
                foreach (TransactionLevel sl in level.Levels)
                {
                    subLevels.Add(ParseLevel(sl));
                }
                lObj["subLevels"] = new JArray(subLevels);
            }

            return lObj;
        }

        // Helper methods moved to CodeParser.cs

        public AnalysisResult AnalyzeInternal(string target)
        {
            try
            {
                Console.Error.WriteLine($"[AnalyzeService] Starting AnalyzeInternal for {target}");
                Console.Error.Flush();
                string xmlContent = null;
                for (int i = 0; i < 3; i++)
                {
                    xmlContent = _objectService.GetObjectXml(target);
                    if (xmlContent != null) break;
                    System.Threading.Thread.Sleep(200);
                    Console.Error.WriteLine($"[AnalyzeService] Retrying GetObjectXml for {target} ({i+1}/3)...");
                }

                if (xmlContent == null) 
                {
                    Console.Error.WriteLine($"[AnalyzeService] Failed to get XML for {target} after retries.");
                    return null;
                }

                Console.Error.WriteLine($"[AnalyzeService] Got XML (len={xmlContent.Length}). Parsing...");
                var doc = new XmlDocument();
                doc.LoadXml(xmlContent);

                string fullCode = "";
                var partNodes = doc.GetElementsByTagName("Part");
                foreach (XmlNode pn in partNodes)
                {
                    var sourceNode = pn.SelectSingleNode("Source");
                    if (sourceNode != null) fullCode += sourceNode.InnerText + "\n";
                }

                string clean = StripComments(fullCode);
                
                // 1. Dependency Analysis (Dual Discovery)
                Console.Error.WriteLine($"[AnalyzeService] Analyzing Calls...");
                var calls = GetCalls(clean);
                Console.Error.WriteLine($"[AnalyzeService] Analyzing Tables...");
                var tables = GetTables(target, xmlContent); 
                
                Console.Error.WriteLine($"[AnalyzeService] Finding Object in SDK...");
                KBObject obj = _objectService.FindObject(target);
                if (obj != null)
                {
                    // SDK-based Discovery
                    foreach (var sc in GetCallsFromSdk(obj)) if (!calls.Contains(sc)) calls.Add(sc);
                    foreach (var st in GetTablesFromSdk(obj)) if (!tables.Contains(st)) tables.Add(st);
                }

                // 2. Metadata & BI
                var tags = GetTags(target, fullCode);
                var rules = GetBusinessRules(clean);
                var domain = GetBusinessDomain(target, tables);
                int complexity = CalculateComplexity(fullCode);

                // 3. Quality Analysis (Linter)
                var insights = AnalyzeQuality(clean, obj);
                
                var messages = new List<string>();
                if (obj != null)
                {
                    try {
                        var getMessagesMethod = obj.GetType().GetMethod("GetMessages", new Type[0]);
                        var sdkMessages = getMessagesMethod?.Invoke(obj, null) as IEnumerable;
                        if (sdkMessages != null) {
                            foreach (object msg in sdkMessages) {
                                messages.Add(msg.ToString());
                            }
                        }
                    } catch {}
                }

                // 4. Update Index
                Console.Error.WriteLine($"[AnalyzeService] Updating Index...");
                UpdateIndex(target, calls, tables, tags, fullCode, complexity, rules, domain);

                return new AnalysisResult
                {
                    Name = target,
                    Calls = calls,
                    Tables = tables,
                    Tags = tags,
                    Rules = rules,
                    Domain = domain,
                    Insights = insights,
                    Messages = messages,
                    Complexity = complexity,
                    CodeLength = fullCode.Length
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AnalyzeService Internal Error] {ex.Message}");
                return null;
            }
        }

        private string StripComments(string code)
        {
            string clean = CommentBlockRegex.Replace(code, "");
            clean = InlineCommentRegex.Replace(clean, "");
            return clean;
        }

        private List<string> GetCalls(string cleanCode)
        {
            var calls = new List<string>();
            var callMatches = CallRegex.Matches(cleanCode);
            foreach (Match m in callMatches)
            {
                string refName = m.Groups[1].Value;
                if (string.IsNullOrEmpty(refName)) refName = m.Groups[2].Value;
                if (!string.IsNullOrEmpty(refName) && !calls.Contains(refName, StringComparer.OrdinalIgnoreCase))
                    calls.Add(refName.Replace("'", ""));
            }
            return calls;
        }

        private List<string> GetCallsFromSdk(KBObject obj)
        {
            var calls = new List<string>();
            try
            {
                var referencesProp = obj.GetType().GetProperty("References");
                var references = referencesProp?.GetValue(obj, null) as System.Collections.IEnumerable;
                if (references != null)
                {
                    foreach (object refObj in references)
                    {
                        var targetNameProp = refObj.GetType().GetProperty("TargetName");
                        string refName = targetNameProp?.GetValue(refObj, null) as string;
                        if (!string.IsNullOrEmpty(refName)) calls.Add(refName);
                    }
                }
            } catch {}
            return calls.Distinct().ToList();
        }

        private List<string> GetTables(string target, string xml)
        {
            var tables = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(xml)) return tables;
                string clean = StripComments(xml);
                tables.AddRange(ForEachRegex.Matches(clean)
                    .Cast<Match>().Select(m => m.Groups[1].Value)
                    .Where(t => t.ToLower() != "where" && t.ToLower() != "order" && t.ToLower() != "definedby")
                    .Distinct());
            } catch {}
            return tables.Distinct().ToList();
        }

        private List<string> GetTablesFromSdk(KBObject obj)
        {
            var tables = new List<string>();
            try
            {
                var referencesProp = obj.GetType().GetProperty("References");
                var references = referencesProp?.GetValue(obj, null) as System.Collections.IEnumerable;
                if (references != null)
                {
                    foreach (object refObj in references)
                    {
                        var targetNameProp = refObj.GetType().GetProperty("TargetName");
                        string refName = targetNameProp?.GetValue(refObj, null) as string;
                        if (!string.IsNullOrEmpty(refName) && !refName.Contains(":") && char.IsUpper(refName[0]))
                            tables.Add(refName);
                    }
                }
            } catch {}
            return tables.Distinct().ToList();
        }

        private List<AnalysisInsight> AnalyzeQuality(string clean, KBObject obj)
        {
            var insights = new List<AnalysisInsight>();

            if (CommitInLoopRegex.IsMatch(clean))
                insights.Add(new AnalysisInsight { Level = "Critical", Message = "COMMIT command detected inside a LOOP. This causing performance issues." });

            if (DynamicCallRegex.IsMatch(clean))
                insights.Add(new AnalysisInsight { Level = "Warning", Message = "Dynamic CALL detected. Breaks reference tracking." });

            if (FullScanRegex.IsMatch(clean) && !WhereClauseRegex.IsMatch(clean))
                insights.Add(new AnalysisInsight { Level = "Critical", Message = "Loop without WHERE clause detected. High risk of Full Table Scan." });

            if (obj != null)
            {
                try {
                    var varsPart = obj.Parts.Get<VariablesPart>();
                    var variables = varsPart?.GetType().GetProperty("Variables")?.GetValue(varsPart, null) as System.Collections.IEnumerable;
                    if (variables != null) {
                        foreach (object vObj in variables) {
                            string vName = vObj.GetType().GetProperty("Name")?.GetValue(vObj, null) as string;
                            bool vIsStd = (bool)(vObj.GetType().GetProperty("IsStandard")?.GetValue(vObj, null) ?? false);
                            if (vIsStd) continue;
                            if (!Regex.IsMatch(clean, @"&\b" + Regex.Escape(vName) + @"\b", RegexOptions.IgnoreCase | RegexOptions.Compiled))
                                insights.Add(new AnalysisInsight { Level = "Warning", Message = $"Unused variable: '&{vName}'" });
                        }
                    }
                } catch {}
            }
            return insights;
        }

        private List<string> GetTags(string target, string code)
        {
            var tags = new List<string>();
            foreach (var r in TagPatterns) if (r.Value.IsMatch(code)) tags.Add(r.Key);
            if (target.StartsWith("Prc", StringComparison.OrdinalIgnoreCase)) tags.Add("Logic-Engine");
            if (target.StartsWith("Trn", StringComparison.OrdinalIgnoreCase)) tags.Add("Data-Model");
            if (target.StartsWith("Wbp", StringComparison.OrdinalIgnoreCase)) tags.Add("UI-Component");
            return tags.Distinct().ToList();
        }

        private List<string> GetBusinessRules(string code)
        {
            var rules = new List<string>();
            if (Regex.IsMatch(code, @"(?i)\berror\s*\(")) rules.Add("Validation Rule");
            if (Regex.IsMatch(code, @"(?i)\bmsg\s*\(")) rules.Add("UI Interaction");
            if (Regex.IsMatch(code, @"(?i)\bcommit\b")) rules.Add("Data Persistence");
            if (Regex.IsMatch(code, @"(?i)\.save\s*\(")) rules.Add("Business Object Update");
            return rules;
        }

        private string GetBusinessDomain(string target, List<string> tables)
        {
            string name = target.Contains(":") ? target.Split(':')[1] : target;
            if (name.StartsWith("Ptc", StringComparison.OrdinalIgnoreCase) || tables.Any(t => t.StartsWith("Ptc", StringComparison.OrdinalIgnoreCase))) return "Protocolo";
            if (name.StartsWith("Fin", StringComparison.OrdinalIgnoreCase) || tables.Any(t => t.StartsWith("Fin", StringComparison.OrdinalIgnoreCase))) return "Financeiro";
            if (name.StartsWith("Acad", StringComparison.OrdinalIgnoreCase) || tables.Any(t => t.StartsWith("Acad", StringComparison.OrdinalIgnoreCase))) return "Acadêmico";
            if (name.StartsWith("Wrf", StringComparison.OrdinalIgnoreCase) || tables.Any(t => t.StartsWith("Wrf", StringComparison.OrdinalIgnoreCase))) return "Workflow/Fluxo";
            return "Geral";
        }

        private int CalculateComplexity(string code)
        {
            int score = 1;
            score += Regex.Matches(code, @"\bif\b", RegexOptions.IgnoreCase).Count;
            score += Regex.Matches(code, @"\bfor\s+each\b", RegexOptions.IgnoreCase).Count * 2;
            return score;
        }

        private void UpdateIndex(string target, List<string> calls, List<string> tables, List<string> tags, string code, int complexity, List<string> rules, string domain)
        {
            try {
                var index = _indexCacheService.GetIndex() ?? new SearchIndex();

                var entry = new SearchIndex.IndexEntry {
                    Name = target,
                    Type = target.Contains(":") ? target.Split(':')[0] : "Unknown",
                    Tags = tags,
                    Calls = calls,
                    Tables = tables,
                    Rules = rules,
                    BusinessDomain = domain,
                    Complexity = complexity,
                    SourceSnippet = code.Length > 200 ? code.Substring(0, 200) : code,
                    FullSource = code,
                    Keywords = target.Replace(":", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList()
                };
                index.Objects[target] = entry;
                index.LastUpdated = DateTime.Now;
                foreach (var call in calls) {
                    if (!index.Objects.ContainsKey(call)) index.Objects[call] = new SearchIndex.IndexEntry { Name = call, Type = call.Contains(":") ? call.Split(':')[0] : "Unknown" };
                    if (index.Objects[call].CalledBy == null) index.Objects[call].CalledBy = new List<string>();
                    if (!index.Objects[call].CalledBy.Contains(target, StringComparer.OrdinalIgnoreCase)) index.Objects[call].CalledBy.Add(target);
                }
                
                _indexCacheService.UpdateIndex(index);
                Console.Error.WriteLine($"[AnalyzeService] Index updated via IndexCacheService.");
                Console.Error.Flush();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AnalyzeService] Index Update Error: {ex.Message}");
                Console.Error.Flush();
            }
        }

        public class AnalysisResult {
            public string Name { get; set; }
            public List<string> Calls { get; set; }
            public List<string> Tables { get; set; }
            public List<string> Tags { get; set; }
            public List<string> Rules { get; set; }
            public string Domain { get; set; }
            public List<AnalysisInsight> Insights { get; set; }
            public List<string> Messages { get; set; } = new List<string>();
            public int Complexity { get; set; }
            public int CodeLength { get; set; }
        }

        public class AnalysisInsight {
            public string Level { get; set; }
            public string Message { get; set; }
        }
    }
}
