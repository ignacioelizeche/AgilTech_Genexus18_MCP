using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Collections.Concurrent;
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
        private sealed class ReadCacheEntry
        {
            public string Payload { get; set; } = string.Empty;
            public DateTime UpdatedUtc { get; set; }
        }

        private static readonly ConcurrentDictionary<string, ReadCacheEntry> _readCache =
            new ConcurrentDictionary<string, ReadCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan ReadCacheTtl = TimeSpan.FromSeconds(20);

        private readonly KbService _kbService;
        private readonly BuildService _buildService;
        private DataInsightService _dataInsightService;
        private UIService _uiService;
        private PatternAnalysisService _patternAnalysisService;
        private WriteService _writeService;

        public ObjectService(KbService kbService, BuildService buildService)
        {
            _kbService = kbService;
            _buildService = buildService;
        }

        public void SetDataInsightService(DataInsightService ds) { _dataInsightService = ds; }
        public void SetUIService(UIService ui) { _uiService = ui; }
        public void SetPatternAnalysisService(PatternAnalysisService patternAnalysisService) { _patternAnalysisService = patternAnalysisService; }
        public void SetWriteService(WriteService writeService) { _writeService = writeService; }
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

        public KBObject FindObject(string target, string typeFilter = null)
        {
            if (string.IsNullOrEmpty(target)) return null;
            var sw = Stopwatch.StartNew();
            var kb = _kbService.GetKB();
            if (kb == null) return null;

            string typePart = typeFilter;
            string namePart = target.Trim();

            if (target.Contains(":") && typeFilter == null)
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
                    // OPTIMIZATION: Prioritize logic types if no filter is provided
                    var matches = new List<SearchIndex.IndexEntry>();
                    foreach (var entry in index.Objects.Values)
                    {
                        if (string.Equals(entry.Name, namePart, StringComparison.OrdinalIgnoreCase))
                        {
                            matches.Add(entry);
                        }
                    }

                    if (matches.Count > 0)
                    {
                        // Order: 1. Non-Folders/Modules, 2. Logic Types (Procedure, WP, Trn), 3. Files/Images (last)
                        var prioritizedMatch = matches
                            .OrderBy(m => (m.Type == "Folder" || m.Type == "Module") ? 100 : 0)
                            .ThenBy(m => (m.Type == "File" || m.Type == "Image") ? 50 : 0)
                            .FirstOrDefault();

                        if (prioritizedMatch != null && !string.IsNullOrEmpty(prioritizedMatch.Guid))
                        {
                             var obj = kb.DesignModel.Objects.Get(new Guid(prioritizedMatch.Guid));
                             if (obj != null) return obj;
                        }
                    }
                }
            }

            // 2. SLOW PATH: Fallback to SDK GetByName (for safety with new objects not yet indexed)
            var sdkMatches = kb.DesignModel.Objects.GetByName(null, null, namePart);
            if (typePart != null)
            {
                foreach (KBObject obj in sdkMatches)
                {
                    if (obj.TypeDescriptor != null && string.Equals(obj.TypeDescriptor.Name, typePart, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Debug(string.Format("FindObject '{0}' SUCCESS (Typed-SDK) in {1}ms", target, sw.ElapsedMilliseconds));
                        return obj;
                    }
                }
            }

            // Global search, prioritizing non-container objects and avoiding Files if others exist
            KBObject firstLogicMatch = null;
            KBObject firstMatch = null;

            foreach (KBObject obj in sdkMatches)
            {
                if (firstMatch == null) firstMatch = obj;
                
                string type = obj.TypeDescriptor?.Name;
                if (type != "Folder" && type != "Module" && type != "File" && type != "Image")
                {
                    if (firstLogicMatch == null) firstLogicMatch = obj;
                }
            }

            var result = firstLogicMatch ?? firstMatch;
            if (result != null)
            {
                Logger.Debug(string.Format("FindObject '{0}' SUCCESS (SDK-Fallback) in {1}ms", target, sw.ElapsedMilliseconds));
            }
            return result;
        }

        public string ExtractAllParts(string target, string client = "ide", string typeFilter = null)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var obj = FindObject(target, typeFilter);
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

        public string ReadObject(string target, string typeFilter = null)
        {
            var obj = FindObject(target, typeFilter);
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

        public string ReadObjectSource(string target, string partName, int? offset = null, int? limit = null, string client = "ide", bool minimize = false, string typeFilter = null)
        {
            var obj = FindObject(target, typeFilter);
            if (obj == null) return HealingService.FormatNotFoundError(target, GetIndex());

            string resolvedPart = ResolvePartName(obj, partName);
            if (ShouldUseReadCache(client, minimize))
            {
                string cacheKey = BuildReadCacheKey(obj.Guid, resolvedPart, offset, limit, client, minimize);
                if (TryGetReadCache(cacheKey, out string cachedPayload))
                {
                    return cachedPayload;
                }

                string payload = ReadObjectSourceInternal(obj, resolvedPart, offset, limit, client, minimize);
                if (CanCachePayload(payload))
                {
                    SetReadCache(cacheKey, payload);
                }

                return payload;
            }

            return ReadObjectSourceInternal(obj, resolvedPart, offset, limit, client, minimize);
        }

        public void MarkReadCacheDirty(KBObject obj, string partName = null)
        {
            if (obj == null)
            {
                return;
            }

            string normalizedPart = string.IsNullOrWhiteSpace(partName) ? null : partName.Trim().ToLowerInvariant();
            string objectPrefix = obj.Guid.ToString("N").ToLowerInvariant() + "|";
            foreach (var key in _readCache.Keys)
            {
                if (!key.StartsWith(objectPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (normalizedPart != null && !key.StartsWith(objectPrefix + normalizedPart + "|", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _readCache.TryRemove(key, out _);
            }

            // SDK object cache invalidation is expensive; do it only after writes.
            InvalidateCache(obj);
        }

        public string ExportObjectToText(string target, string outputPath, string partName = null, string typeFilter = null, bool overwrite = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(outputPath))
                    return Models.McpResponse.Error("Output path is required.", target);

                var obj = FindObject(target, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(target, GetIndex());

                string normalizedPart = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;
                string exportJson = ReadObjectSourceInternal(obj, normalizedPart, 0, int.MaxValue, "mcp", false);
                JObject exportResult = JObject.Parse(exportJson);
                string source = exportResult["source"]?.ToString();
                if (string.IsNullOrEmpty(source))
                {
                    return Models.McpResponse.Error(
                        "Export failed",
                        target,
                        normalizedPart,
                        exportResult["error"]?.ToString() ?? "The object part did not return text content.",
                        obj.Name,
                        obj.TypeDescriptor?.Name);
                }

                string fullPath = Path.GetFullPath(outputPath);
                string directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                if (File.Exists(fullPath) && !overwrite)
                    return Models.McpResponse.Error("Output file already exists. Set overwrite=true to replace it.", fullPath);

                File.WriteAllText(fullPath, source, new UTF8Encoding(false));
                return Models.McpResponse.Success("ExportText", target, new JObject
                {
                    ["part"] = normalizedPart,
                    ["path"] = fullPath,
                    ["bytes"] = new FileInfo(fullPath).Length
                });
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Error("Export failed", target, partName, ex.Message);
            }
        }

        public string ImportObjectFromText(string target, string inputPath, string partName = null, string typeFilter = null)
        {
            try
            {
                if (_writeService == null)
                    return Models.McpResponse.Error("Import failed", target, partName, "Write service is not available.");

                if (string.IsNullOrWhiteSpace(inputPath))
                    return Models.McpResponse.Error("Input path is required.", target);

                string fullPath = Path.GetFullPath(inputPath);
                if (!File.Exists(fullPath))
                    return Models.McpResponse.Error("Input file not found.", fullPath);

                string normalizedPart = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;
                var obj = FindObject(target, typeFilter);
                if (obj == null)
                {
                    if (string.IsNullOrWhiteSpace(typeFilter))
                    {
                        return Models.McpResponse.Error(
                            "Import failed",
                            target,
                            normalizedPart,
                            "Object not found. Provide 'type' to create it before importing.");
                    }

                    string createResult = CreateObject(typeFilter, target);
                    JObject createJson = JObject.Parse(createResult);
                    if (!string.Equals(createJson["status"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase))
                    {
                        return createResult;
                    }
                }

                string importedText = File.ReadAllText(fullPath);
                string writeResult = _writeService.WriteObject(target, normalizedPart, importedText, typeFilter, autoValidate: false);
                JObject writeJson = JObject.Parse(writeResult);

                if (string.Equals(writeJson["status"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase))
                {
                    writeJson["path"] = fullPath;
                    writeJson["part"] = normalizedPart;
                    writeJson["importedBytes"] = new FileInfo(fullPath).Length;
                }

                return writeJson.ToString();
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Error("Import failed", target, partName, ex.Message);
            }
        }

        private string ReadObjectSourceInternal(KBObject obj, string partName, int? offset = null, int? limit = null, string client = "ide", bool minimize = false)
        {
            partName = ResolvePartName(obj, partName);

            Logger.Info($"ReadObjectSourceInternal: {obj.Name} (Part: {partName}, Client: {client})");
            var sw = Stopwatch.StartNew();
            try
            {
                string targetName = obj.Name;

                if (WebFormXmlHelper.IsVisualPart(partName))
                {
                    string xml = WebFormXmlHelper.ReadEditableXml(obj);
                    if (string.IsNullOrEmpty(xml))
                    {
                        return Models.McpResponse.Error(
                            "Visual XML not available",
                            targetName,
                            partName,
                            "The object does not expose editable WebForm XML through the current SDK path.",
                            obj.Name,
                            obj.TypeDescriptor?.Name,
                            new JArray(GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj)));
                    }

                    var visualResult = new JObject
                    {
                        ["part"] = partName,
                        ["contentType"] = "application/xml",
                        ["xmlKind"] = "GxMultiForm"
                    };
                    ProcessTextResponse(xml, visualResult, client);
                    return visualResult.ToString();
                }

                if (PatternAnalysisService.IsPatternPart(partName))
                {
                    global::Artech.Architecture.Common.Objects.KBObject resolvedObject = null;
                    string resolvedPartName = partName;
                    string patternXml = _patternAnalysisService?.ReadPatternPartXml(obj, partName, out resolvedObject, out resolvedPartName);
                    if (string.IsNullOrEmpty(patternXml))
                    {
                        return Models.McpResponse.Error(
                            "Pattern XML not available",
                            targetName,
                            partName,
                            "The requested WorkWithPlus pattern part could not be resolved through the current SDK path.",
                            obj.Name,
                            obj.TypeDescriptor?.Name,
                            new JArray(GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj)));
                    }

                    var patternResult = new JObject
                    {
                        ["part"] = partName,
                        ["contentType"] = "application/xml",
                        ["xmlKind"] = resolvedPartName
                    };

                    if (resolvedObject != null && resolvedObject.Guid != obj.Guid)
                    {
                        patternResult["resolvedObject"] = resolvedObject.Name;
                        patternResult["resolvedType"] = resolvedObject.TypeDescriptor?.Name;
                    }

                    ProcessTextResponse(patternXml, patternResult, client);
                    return patternResult.ToString();
                }

                Guid partGuid = GxMcp.Worker.Structure.PartAccessor.GetPartGuid(obj.TypeDescriptor.Name, partName);
                
                KBObjectPart part = GxMcp.Worker.Structure.PartAccessor.GetPart(obj, partName);

                JObject result = new JObject();
                result["part"] = partName;

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

        private static string ResolvePartName(KBObject obj, string partName)
        {
            if (!string.IsNullOrWhiteSpace(partName))
            {
                return partName;
            }

            if (obj is Procedure) return "Source";
            if (obj is Transaction || obj is WebPanel) return "Events";
            return "Source";
        }

        private static bool ShouldUseReadCache(string client, bool minimize)
        {
            return string.Equals(client, "mcp", StringComparison.OrdinalIgnoreCase) && !minimize;
        }

        private static string BuildReadCacheKey(Guid objectGuid, string partName, int? offset, int? limit, string client, bool minimize)
        {
            string normalizedPart = string.IsNullOrWhiteSpace(partName) ? "source" : partName.Trim().ToLowerInvariant();
            string normalizedClient = string.IsNullOrWhiteSpace(client) ? "mcp" : client.Trim().ToLowerInvariant();
            int normalizedOffset = offset ?? -1;
            int normalizedLimit = limit ?? -1;
            return objectGuid.ToString("N").ToLowerInvariant() + "|" + normalizedPart + "|" + normalizedOffset + "|" + normalizedLimit + "|" + normalizedClient + "|" + (minimize ? "1" : "0");
        }

        private static bool TryGetReadCache(string key, out string payload)
        {
            payload = string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            if (!_readCache.TryGetValue(key, out var entry) || entry == null)
            {
                return false;
            }

            if (DateTime.UtcNow - entry.UpdatedUtc > ReadCacheTtl)
            {
                _readCache.TryRemove(key, out _);
                return false;
            }

            payload = entry.Payload;
            return !string.IsNullOrWhiteSpace(payload);
        }

        private static void SetReadCache(string key, string payload)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(payload))
            {
                return;
            }

            _readCache[key] = new ReadCacheEntry
            {
                Payload = payload,
                UpdatedUtc = DateTime.UtcNow
            };
        }

        private static bool CanCachePayload(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            try
            {
                var json = JObject.Parse(payload);
                return json["error"] == null;
            }
            catch
            {
                return false;
            }
        }

        private void ProcessSourceContent(KBObject obj, string content, int? offset, int? limit, JObject result, string client = "ide")
        {
            // Performance: Use StringReader to process line by line without immediate massive splitting
            using (var reader = new StringReader(content))
            {
                int start = offset ?? 0;
                int count = limit ?? int.MaxValue;
                bool includeDerivedMetadata = client != "mcp";
                
                if (!offset.HasValue && !limit.HasValue && client == "mcp")
                {
                    start = 0;
                    count = 200;
                    result["isTruncatedByWorker"] = true;
                    result["message"] = "MCP read defaulted to the first 200 lines to control context size. Use genexus_read with offset/limit to paginate.";
                }

                var paginatedLines = new List<string>();
                int currentLine = 0;
                string line;
                int totalLinesInFile = 0;

                while ((line = reader.ReadLine()) != null)
                {
                    if (currentLine >= start && paginatedLines.Count < count)
                    {
                        paginatedLines.Add(line);
                    }
                    currentLine++;
                    totalLinesInFile = currentLine;
                    
                    // Optimization: If we already have what we need AND we don't need total count, we could break.
                    // But for metadata accuracy, we continue to count totalLinesInFile unless it's too much.
                    if (paginatedLines.Count >= count && totalLinesInFile > 10000) break; 
                }

                string paginatedContent = string.Join(Environment.NewLine, paginatedLines);

                if (paginatedLines.Count < totalLinesInFile && !result.ContainsKey("isTruncatedByWorker"))
                {
                    paginatedContent += "\n\n// ... [CONTENT TRUNCATED. USE PAGINATION (offset/limit) TO READ FURTHER] ... //\n";
                }

                ProcessTextResponse(paginatedContent, result, client);
                result["offset"] = start;
                result["limit"] = paginatedLines.Count;
                result["totalLines"] = totalLinesInFile;

                if (includeDerivedMetadata)
                {
                    AddVariableMetadata(obj, paginatedContent, result);
                    AddCallSignatures(obj, paginatedContent, result);
                }
            }
        }

        private void ProcessTextResponse(string text, JObject result, string client)
        {
            result["isEmpty"] = string.IsNullOrEmpty(text);

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

        private static void InvalidateCache(object obj)
        {
            try
            {
                var type = typeof(Artech.Architecture.Common.Objects.KBObject).Assembly.GetType("Artech.Architecture.Common.Cache.SingleInstanceModelObjectCache");
                if (type != null)
                {
                    var method = type.GetMethod("Invalidate", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    method?.Invoke(null, new object[] { obj });
                    Logger.Debug("InvalidateCache: Object invalidated via reflection.");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("InvalidateCache reflection failed: " + ex.Message);
            }
        }
    }
}
