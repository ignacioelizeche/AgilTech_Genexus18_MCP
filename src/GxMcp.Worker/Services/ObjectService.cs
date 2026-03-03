using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Descriptors;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class ObjectService
    {
        private readonly KbService _kbService;
        private readonly BuildService _buildService;
        private DataInsightService _dataInsightService;
        private UIService _uiService;

        public ObjectService(KbService kbService, BuildService buildService)
        {
            _kbService = kbService;
            _buildService = buildService;
        }

        public void SetDataInsightService(DataInsightService ds) { _dataInsightService = ds; }
        public void SetUIService(UIService ui) { _uiService = ui; }
        public KbService GetKbService() { return _kbService; }

        public SearchIndex GetIndex() { return _kbService.GetIndexCache().GetIndex(); }

        public string CreateObject(string type, string name)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var kb = _kbService.GetKB();
                if (kb == null) return "{\"status\":\"Error\", \"error\":\"No KB open\"}";

                Logger.Info(string.Format("Creating Object: {0} ({1})", name, type));

                // Map string type to Guid
                Guid typeGuid = Guid.Empty;
                if (type.Equals("Procedure", StringComparison.OrdinalIgnoreCase)) typeGuid = KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Procedure>().Id;
                else if (type.Equals("Transaction", StringComparison.OrdinalIgnoreCase)) typeGuid = KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Transaction>().Id;
                else if (type.Equals("WebPanel", StringComparison.OrdinalIgnoreCase)) typeGuid = KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.WebPanel>().Id;
                else if (type.Equals("SDT", StringComparison.OrdinalIgnoreCase) || type.Equals("StructuredDataType", StringComparison.OrdinalIgnoreCase)) typeGuid = KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.SDT>().Id;
                else if (type.Equals("DataProvider", StringComparison.OrdinalIgnoreCase)) typeGuid = KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.DataProvider>().Id;
                else if (type.Equals("Attribute", StringComparison.OrdinalIgnoreCase)) typeGuid = KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Attribute>().Id;
                else if (type.Equals("Table", StringComparison.OrdinalIgnoreCase)) typeGuid = KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Table>().Id;
                else if (type.Equals("SDPanel", StringComparison.OrdinalIgnoreCase)) typeGuid = Guid.Parse("702119eb-90e9-4e78-958b-96d5e182283a"); // SDPanel Guid

                if (typeGuid == Guid.Empty) return "{\"status\":\"Error\", \"error\":\"Unsupported object type: " + type + "\"}";

                KBObject newObj = KBObject.Create(kb.DesignModel, typeGuid);
                newObj.Name = name;
                
                // Initialize with some default content if possible
                if (newObj.GetType().Name == "Procedure")
                {
                    var partProp = newObj.GetType().GetProperty("ProcedurePart");
                    if (partProp != null) {
                        object part = partProp.GetValue(newObj);
                        if (part != null) {
                            var sourceProp = part.GetType().GetProperty("Source");
                            if (sourceProp != null) sourceProp.SetValue(part, "// Procedure: " + name + "\n\n");
                        }
                    }
                }
                else if (newObj.GetType().Name == "DataProvider")
                {
                    var partsProp = newObj.GetType().GetProperty("Parts");
                    if (partsProp != null) {
                        var parts = (System.Collections.IEnumerable)partsProp.GetValue(newObj);
                        foreach (object p in parts)
                        {
                            if (p.GetType().Name == "SourcePart")
                            {
                                var sourceProp = p.GetType().GetProperty("Source");
                                if (sourceProp != null) sourceProp.SetValue(p, "// Data Provider: " + name + "\n\n");
                                break;
                            }
                        }
                    }
                }

                newObj.Save();
                
                Logger.Info(string.Format("Object created successfully in {0}ms", sw.ElapsedMilliseconds));
                return "{\"status\":\"Success\", \"type\":\"" + type + "\", \"name\":\"" + name + "\"}";
            }
            catch (Exception ex)
            {
                Logger.Error("CreateObject failed: " + ex.Message);
                return "{\"status\":\"Error\", \"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public KBObject FindObject(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;
            var sw = Stopwatch.StartNew();
            var kb = _kbService.GetKB();
            if (kb == null) return null;

            string typePart = null;
            string namePart = target.Trim();

            if (target.Contains(":"))
            {
                var parts = target.Split(':');
                typePart = parts[0].Trim();
                namePart = parts[1].Trim();
            }

            // 1. FAST PATH: Use Search Index
            var index = GetIndex();
            if (index != null && index.Objects != null)
            {
                if (typePart != null)
                {
                    string key = string.Format("{0}:{1}", typePart, namePart);
                    if (index.Objects.TryGetValue(key, out var entry) && !string.IsNullOrEmpty(entry.Guid))
                    {
                        var obj = kb.DesignModel.Objects.Get(new Guid(entry.Guid));
                        if (obj != null) {
                            Logger.Debug(string.Format("FindObject '{0}' SUCCESS (Index-Typed) in {1}ms", target, sw.ElapsedMilliseconds));
                            return obj;
                        }
                    }
                }
                else
                {
                    // Global search in index
                    foreach (var entry in index.Objects.Values)
                    {
                        if (string.Equals(entry.Name, namePart, StringComparison.OrdinalIgnoreCase))
                        {
                            // Favor non-folders/modules
                            if (entry.Type != "Folder" && entry.Type != "Module" && !string.IsNullOrEmpty(entry.Guid))
                            {
                                var obj = kb.DesignModel.Objects.Get(new Guid(entry.Guid));
                                if (obj != null) {
                                    Logger.Debug(string.Format("FindObject '{0}' SUCCESS (Index-Global) in {1}ms", target, sw.ElapsedMilliseconds));
                                    return obj;
                                }
                            }
                        }
                    }
                }
            }

            // 2. SLOW PATH: Fallback to SDK GetByName (for safety with new objects not yet indexed)
            if (typePart != null)
            {
                foreach (KBObject obj in kb.DesignModel.Objects.GetByName(null, null, namePart))
                {
                    if (string.Equals(obj.TypeDescriptor.Name, typePart, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Debug(string.Format("FindObject '{0}' SUCCESS (Typed-SDK) in {1}ms", target, sw.ElapsedMilliseconds));
                        return obj;
                    }
                }
            }

            // Global search, prioritizing non-container objects
            KBObject firstMatch = null;
            foreach (KBObject obj in kb.DesignModel.Objects.GetByName(null, null, namePart))
            {
                if (firstMatch == null) firstMatch = obj;
                
                string type = obj.TypeDescriptor.Name;
                if (type != "Folder" && type != "Module")
                {
                    Logger.Debug(string.Format("FindObject '{0}' SUCCESS (Global-SDK) in {1}ms", target, sw.ElapsedMilliseconds));
                    return obj;
                }
            }

            if (firstMatch != null)
            {
                Logger.Debug(string.Format("FindObject '{0}' SUCCESS (Container-SDK) in {1}ms", target, sw.ElapsedMilliseconds));
            }
            return firstMatch;
        }

        public string ExtractAllParts(string target, string client = "ide")
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var obj = FindObject(target);
                if (obj == null) return HealingService.FormatNotFoundError(target, GetIndex());

                var result = new JObject { ["name"] = obj.Name, ["parts"] = new JObject() };
                string[] partsToFetch = { "Source", "Rules", "Events", "Variables", "Documentation", "Help" };

                foreach (var pName in partsToFetch)
                {
                    string partJson = ReadObjectSourceInternal(obj, pName, null, null, client);
                    try {
                        var pObj = JObject.Parse(partJson);
                        if (pObj["source"] != null)
                        {
                            ((JObject)result["parts"])[pName] = pObj["source"];
                        }
                    } catch { }
                }

                Logger.Info(string.Format("ExtractAllParts for {0} complete in {1}ms", obj.Name, sw.ElapsedMilliseconds));
                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string ReadObject(string target)
        {
            var obj = FindObject(target);
            if (obj == null) return HealingService.FormatNotFoundError(target, GetIndex());

            var parts = new JArray();
            foreach (KBObjectPart p in obj.Parts)
            {
                parts.Add(new JObject { 
                    ["name"] = p.TypeDescriptor?.Name ?? p.Type.ToString(), 
                    ["guid"] = p.Type.ToString() 
                });
            }

            string parentName = null;
            string moduleName = null;

            try { parentName = obj.Parent?.Name; } catch { }
            try { moduleName = obj.Module?.Name; } catch { }

            return new JObject { 
                ["name"] = obj.Name, 
                ["type"] = obj.TypeDescriptor.Name,
                ["parent"] = parentName,
                ["module"] = moduleName,
                ["parts"] = parts
            }.ToString();
        }

        public string ReadObjectSource(string target, string partName, int? offset = null, int? limit = null, string client = "ide", bool minimize = false)
        {
            var obj = FindObject(target);
            if (obj == null) return HealingService.FormatNotFoundError(target, GetIndex());
            return ReadObjectSourceInternal(obj, partName, offset, limit, client, minimize);
        }

        private string ReadObjectSourceInternal(KBObject obj, string partName, int? offset = null, int? limit = null, string client = "ide", bool minimize = false)
        {
            if (string.IsNullOrEmpty(partName))
            {
                if (obj is Procedure) partName = "Source";
                else if (obj is Transaction || obj is WebPanel) partName = "Events";
                else partName = "Source";
            }

            Logger.Info($"ReadObjectSourceInternal: {obj.Name} (Part: {partName}, Client: {client})");
            var sw = Stopwatch.StartNew();
            try
            {
                string targetName = obj.Name;

                // Special handling for Layout
                if (partName.Equals("layout", StringComparison.OrdinalIgnoreCase))
                {
                    var uiContextJson = _uiService.GetUIContext(obj.Name);
                    var uiObj = JObject.Parse(uiContextJson);
                    if (uiObj["html"] != null)
                    {
                        var layoutResult = new JObject();
                        layoutResult["source"] = uiObj["html"].ToString();
                        return layoutResult.ToString();
                    }
                }

                Guid partGuid = GxMcp.Worker.Structure.PartAccessor.GetPartGuid(obj.TypeDescriptor.Name, partName);
                
                KBObjectPart part = GxMcp.Worker.Structure.PartAccessor.GetPart(obj, partName);

                JObject result = new JObject();

                // Virtual/DSL Parts (Structure for Trn/Table/SDT) 
                // We process this BEFORE the generic part check because Tables might not have a physical Part GUID mapped,
                // and even if they do, we want our custom DSL representation.
                if (partName.Equals("Structure", StringComparison.OrdinalIgnoreCase) && 
                        (obj.GetType().Name == "Transaction" || obj.GetType().Name == "Table" || obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase)))
                {
                    string structureText = StructureParser.SerializeToText(obj);
                    ProcessTextResponse(structureText, result, client);
                    Logger.Info("ReadSource (Structure DSL) SUCCESS");
                    return result.ToString();
                }

                if (part == null) 
                {
                    result["error"] = $"Part '{partName}' not found in {obj.Name}";
                    return result.ToString();
                }

                // Handle Variables Part specially
                if (part.GetType().Name.Equals("VariablesPart"))
                {
                    string varText = VariableInjector.GetVariablesAsText((dynamic)part);
                    ProcessTextResponse(varText, result, client);
                    Logger.Info("ReadSource (Variables) SUCCESS");
                }
                else if (part is ISource sourcePart)
                {
                    string content = sourcePart.Source ?? "";
                    if (minimize && content.Length > 5000)
                    {
                        content = content.Substring(0, 2500) + "\n... [TRUNCATED FOR BREVITY - USE PAGINATION] ...\n" + content.Substring(content.Length - 1000);
                    }
                    ProcessSourceContent(obj, content, offset, limit, result, client);
                    Logger.Info("ReadSource (ISource) SUCCESS");
                }
                else
                {
                    // Reflection Fallback for Data Providers and other parts that encapsulate a Source string but don't implement ISource natively.
                    var contentProp = part.GetType().GetProperty("Source", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                   ?? part.GetType().GetProperty("Content", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                    if (contentProp != null && contentProp.CanRead && contentProp.PropertyType == typeof(string))
                    {
                        string content = (string)contentProp.GetValue(part) ?? "";
                        if (minimize && content.Length > 5000)
                        {
                            content = content.Substring(0, 2500) + "\n... [TRUNCATED FOR BREVITY] ...\n" + content.Substring(content.Length - 1000);
                        }
                        ProcessSourceContent(obj, content, offset, limit, result, client);
                        Logger.Info("ReadSource (Reflection) SUCCESS");
                    }
                    else
                    {
                        string xml = part.SerializeToXml();
                        ProcessTextResponse(xml, result, client);
                        Logger.Info("ReadSource (XML) SUCCESS");
                    }
                }

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private void ProcessSourceContent(KBObject obj, string content, int? offset, int? limit, JObject result, string client = "ide")
        {
            if (offset.HasValue || limit.HasValue)
            {
                string[] lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                int start = offset ?? 0;
                int count = limit ?? (lines.Length - start);

                if (start < 0) start = 0;
                if (start >= lines.Length) count = 0;
                else if (start + count > lines.Length) count = lines.Length - start;

                string paginatedContent = string.Join(Environment.NewLine, lines.Skip(start).Take(count));
                ProcessTextResponse(paginatedContent, result, client);
                result["offset"] = start;
                result["limit"] = count;
                result["totalLines"] = lines.Length;

                AddVariableMetadata(obj, paginatedContent, result);
                AddCallSignatures(obj, paginatedContent, result);
            }
            else
            {
                ProcessTextResponse(content, result, client);
                AddVariableMetadata(obj, content, result);
                AddCallSignatures(obj, content, result);
            }
        }

        private void ProcessTextResponse(string text, JObject result, string client)
        {
            if (client == "mcp")
            {
                Logger.Debug("ProcessTextResponse: Using Plain Text for MCP");
                result["source"] = text;
                result["isBase64"] = false;
            }
            else
            {
                Logger.Debug($"ProcessTextResponse: Using Base64 for {client}");
                result["source"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
                result["isBase64"] = true;
            }
        }

        private void AddCallSignatures(KBObject obj, string source, JObject result)
        {
            try
            {
                var calledObjectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Regex for common call patterns in GeneXus
                var callMatches = System.Text.RegularExpressions.Regex.Matches(source, 
                    @"\b(?:call|udp|submit)\s*\(\s*(\w+)|\b(\w+)\s*\.\s*(?:call|udp|submit)\b", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                foreach (System.Text.RegularExpressions.Match match in callMatches)
                {
                    string name = !string.IsNullOrEmpty(match.Groups[1].Value) ? match.Groups[1].Value : match.Groups[2].Value;
                    calledObjectNames.Add(name);
                }

                if (calledObjectNames.Count > 0)
                {
                    var calls = new JArray();
                    foreach (var name in calledObjectNames)
                    {
                        var target = FindObject(name);
                        if (target != null && target.Guid != obj.Guid)
                        {
                            var (parmRule, parms) = GetParametersInternal(target);
                            if (!string.IsNullOrEmpty(parmRule))
                            {
                                var cObj = new JObject();
                                cObj["name"] = target.Name;
                                cObj["type"] = target.TypeDescriptor.Name;
                                cObj["parmRule"] = parmRule;
                                calls.Add(cObj);
                            }
                        }
                    }
                    if (calls.Count > 0) result["calls"] = calls;
                }
            }
            catch (Exception ex) { Logger.Debug("AddCallSignatures failed: " + ex.Message); }
        }

        private void AddVariableMetadata(KBObject obj, string source, JObject result)
        {
            try
            {
                // Nirvana v19.4: Auto-Inject Full Context (Variables + Data Schema + Pattern)
                var varPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.GetType().Name.Equals("VariablesPart"));
                if (varPart != null)
                {
                    var referencedVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var matches = System.Text.RegularExpressions.Regex.Matches(source, @"&(\w+)");
                    foreach (System.Text.RegularExpressions.Match match in matches) {
                        referencedVars.Add(match.Groups[1].Value);
                    }

                    if (referencedVars.Count > 0)
                    {
                        var variables = new JArray();
                        var varListProp = varPart.GetType().GetProperty("Variables");
                        if (varListProp != null)
                        {
                            var varList = varListProp.GetValue(varPart) as System.Collections.IEnumerable;
                            if (varList != null)
                            {
                                foreach (object vObj in varList)
                                {
                                    dynamic v = vObj;
                                    string vName = v.Name;
                                    if (referencedVars.Contains(vName))
                                    {
                                        variables.Add(new JObject {
                                            ["name"] = vName,
                                            ["type"] = v.Type.ToString(),
                                            ["length"] = Convert.ToInt32(v.Length),
                                            ["decimals"] = Convert.ToInt32(v.Decimals),
                                            ["isCollection"] = (bool)v.IsCollection
                                        });
                                    }
                                }
                            }
                        }
                        if (variables.Count > 0) result["variables"] = variables;
                    }
                }

                // Inject Data Context (Tables used in this object)
                if (_dataInsightService != null)
                {
                    try {
                        var dataContextJson = _dataInsightService.GetDataContext(obj.Name);
                        if (!string.IsNullOrEmpty(dataContextJson) && !dataContextJson.Contains("\"error\""))
                        {
                            var dataContext = JObject.Parse(dataContextJson);
                            if (dataContext["dataSchema"] != null) result["dataSchema"] = dataContext["dataSchema"];
                            if (dataContext["patternMetadata"] != null) result["patternMetadata"] = dataContext["patternMetadata"];
                        }
                    } catch { /* Silent fail to ensure read stability */ }
                }
            }
            catch { }
        }

        public class ParameterInfo
        {
            public string Name { get; set; }
            public string Accessor { get; set; }
            public string Type { get; set; }
        }

        public (string parmRule, List<ParameterInfo> parameters) GetParametersInternal(KBObject obj)
        {
            string parmRule = "";
            var parameters = new List<ParameterInfo>();

            try
            {
                if (obj is Procedure proc) parmRule = proc.Rules.Source.Split('\n').FirstOrDefault(l => l.Trim().StartsWith("parm(", StringComparison.OrdinalIgnoreCase));
                else if (obj is Transaction trn) parmRule = trn.Rules.Source.Split('\n').FirstOrDefault(l => l.Trim().StartsWith("parm(", StringComparison.OrdinalIgnoreCase));
                else if (obj is WebPanel wp) parmRule = wp.Rules.Source.Split('\n').FirstOrDefault(l => l.Trim().StartsWith("parm(", StringComparison.OrdinalIgnoreCase));
                else if (obj is DataProvider dp) parmRule = (string)dp.GetType().GetProperty("Rules")?.GetValue(dp)?.GetType().GetProperty("Source")?.GetValue(((dynamic)dp).Rules) ?? "";
                
                if (string.IsNullOrEmpty(parmRule) && obj is DataProvider dp2) {
                    // DataProvider might have parm in Source instead of Rules in some versions/objects
                    try { 
                        string sourceStr = ((dynamic)dp2).Source.Source;
                        foreach (string line in sourceStr.Split('\n'))
                        {
                            if (line.Trim().StartsWith("parm(", StringComparison.OrdinalIgnoreCase))
                            {
                                parmRule = line;
                                break;
                            }
                        }
                    } catch {}
                }

                if (!string.IsNullOrEmpty(parmRule))
                {
                    parmRule = parmRule.Trim();
                    var match = System.Text.RegularExpressions.Regex.Match(parmRule, @"parm\s*\((.*)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        var parmContent = match.Groups[1].Value;
                        var parts = parmContent.Split(',');
                        foreach (var part in parts)
                        {
                            var p = part.Trim();
                            var pInfo = new ParameterInfo { Name = p, Accessor = "in", Type = "Unknown" };
                            if (p.StartsWith("inout:", StringComparison.OrdinalIgnoreCase)) { pInfo.Accessor = "inout"; pInfo.Name = p.Substring(6).Trim(); }
                            else if (p.StartsWith("in:", StringComparison.OrdinalIgnoreCase)) { pInfo.Accessor = "in"; pInfo.Name = p.Substring(3).Trim(); }
                            else if (p.StartsWith("out:", StringComparison.OrdinalIgnoreCase)) { pInfo.Accessor = "out"; pInfo.Name = p.Substring(4).Trim(); }
                            
                            if (pInfo.Name.StartsWith("&")) pInfo.Name = pInfo.Name.Substring(1);
                            parameters.Add(pInfo);
                        }
                    }
                }
            }
            catch { }

            return (parmRule, parameters);
        }
    }
}
