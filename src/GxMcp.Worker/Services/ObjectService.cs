using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using Artech.Architecture.Common;
using Artech.Architecture.UI.Framework.Services;
using Artech.Udm.Framework;
using Artech.Genexus.Common;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using Newtonsoft.Json.Linq;
using System.Xml;
using GxMcp.Worker.Helpers;
using Attribute = Artech.Genexus.Common.Objects.Attribute;

namespace GxMcp.Worker.Services
{
    public class ObjectService
    {
        private BuildService _buildService;
        private KbService _kbService;

        // Cache: Key=ObjectName, Value=XmlContent
        private static readonly Dictionary<string, string> _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> _lru = new List<string>();
        private const int MAX_CACHE_SIZE = 50;
        private readonly string _diskCacheDir;

        // GeneXus Part GUIDs
        private static readonly Dictionary<string, Guid> _partGuids = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            { "Source", new Guid("528d1c06-a9c2-420d-bd35-21dca83f12ff") }, // Procedure Source
            { "Rules", new Guid("c414ed00-8cc4-4f44-8820-4baf93547173") },  // Rules
            { "Events", new Guid("e4c4ade7-53f0-4a56-bdfd-843735b66f47") }, // Events
            { "Conditions", new Guid("ad3ca970-19d0-44e1-a7b7-db05556e820c") }, // Conditions
            { "Variables", new Guid("9b0a32a3-de6d-4be1-a4dd-1b85d3741534") } // Variables
        };

        public ObjectService(BuildService buildService, KbService kbService)
        {
            _buildService = buildService;
            _kbService = kbService;
            _diskCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "objects");
            if (!Directory.Exists(_diskCacheDir)) Directory.CreateDirectory(_diskCacheDir);
        }

        public KnowledgeBase GetKB()
        {
            return _kbService.GetKB();
        }

        public void ClearCache()
        {
            _cache.Clear();
            _lru.Clear();
            Console.Error.WriteLine("[ObjectService] Cache cleared.");
        }

        public void Reload()
        {
            ClearCache();
            _kbService.Reload();
        }

        private KnowledgeBase EnsureKbOpen()
        {
            return _kbService.GetKB();
        }

        private string GetGuid(object obj)
        {
            if (obj == null) return "";
            
            // If it is already a Guid, just return it
            if (obj is Guid) return obj.ToString();

            // Try Guid property
            var guidProp = obj.GetType().GetProperty("Guid");
            if (guidProp != null) return guidProp.GetValue(obj, null)?.ToString() ?? "";

            // Try Id property
            var idProp = obj.GetType().GetProperty("Id");
            if (idProp != null) return idProp.GetValue(obj, null)?.ToString() ?? "";

            // Try TypeGuid if it's a Part or similar
            var typeGuidProp = obj.GetType().GetProperty("TypeGuid");
            if (typeGuidProp != null) return typeGuidProp.GetValue(obj, null)?.ToString() ?? "";

            return "";
        }

        public Guid GetObjectId(string name)
        {
            var obj = FindObject(name);
            return obj != null ? obj.Guid : Guid.Empty;
        }

        public KBObject FindObject(string target)
        {
            Console.Error.WriteLine($"[ObjectService] FindObject: {target}");
            var kb = EnsureKbOpen();
            if (kb == null) return null;

            string namePart = target;
            Guid? expectedType = null;

            if (target.Contains(":"))
            {
                var split = target.Split(':');
                string prefix = split[0].ToLower();
                namePart = split[1];

                if (prefix == "trn") expectedType = Artech.Genexus.Common.ObjClass.Transaction;
                else if (prefix == "prc") expectedType = Artech.Genexus.Common.ObjClass.Procedure;
                else if (prefix == "wp" || prefix == "webpanel") expectedType = Artech.Genexus.Common.ObjClass.WebPanel;
                else if (prefix == "att" || prefix == "attribute") expectedType = Artech.Genexus.Common.ObjClass.Attribute;
                else if (prefix == "tbl" || prefix == "table") expectedType = Artech.Genexus.Common.ObjClass.Table;
                else if (prefix == "dom" || prefix == "domain") expectedType = Artech.Genexus.Common.ObjClass.Domain;
            }

            if (expectedType != null)
            {
                // Indexed lookup - MUCH FASTER
                return kb.DesignModel.Objects.Get(expectedType.Value, new QualifiedName(namePart));
            }

            // Fallback lookup by name only if type is unknown
            var objects = kb.DesignModel.Objects as IEnumerable;
            if (objects == null) return null;

            foreach (object o in objects)
            {
                KBObject kbo = o as KBObject;
                if (kbo != null && string.Equals(kbo.Name, namePart, StringComparison.OrdinalIgnoreCase))
                {
                    return kbo;
                }
            }
            return null;
        }

        public string GetObjectXml(string target)
        {
            Console.Error.WriteLine($"[ObjectService] GetObjectXml: {target}");
            if (string.IsNullOrEmpty(target)) return null;

            // L1 Cache check
            if (_cache.ContainsKey(target))
            {
                _lru.Remove(target);
                _lru.Add(target);
                return _cache[target];
            }

            // L2 Cache (Disk) check
            string safeName = target.Replace(":", "_");
            string cachePath = Path.Combine(_diskCacheDir, safeName + ".xml");
            if (File.Exists(cachePath))
            {
                try {
                    string cachedXml = File.ReadAllText(cachePath);
                    if (!string.IsNullOrEmpty(cachedXml))
                    {
                        AddToCache(target, cachedXml, false); // Add to L1 without re-saving to disk
                        return cachedXml;
                    }
                } catch { }
            }

            EnsureKbOpen();

            KBObject obj = FindObject(target);
            if (obj == null) return null;

            try 
            {
                XElement root = new XElement("Object",
                    new XAttribute("guid", obj.Guid.ToString()),
                    new XAttribute("name", obj.Name),
                    new XAttribute("type", GetGuid(obj.TypeDescriptor)),
                    new XElement("Description", obj.Description)
                );

                XElement partsNode = new XElement("Parts");
                var parts = obj.Parts as IEnumerable;
                if (parts != null)
                {
                    foreach (object p in parts)
                    {
                        string innerXml = "";
                        string partId = "";
                        string partName = "";
                        try
                        {
                            var typeProp = p.GetType().GetProperty("Type");
                            var partType = typeProp?.GetValue(p, null);
                            partId = GetGuid(partType);
                            
                            var ptNameProp = partType?.GetType().GetProperty("Name");
                            partName = ptNameProp?.GetValue(partType, null) as string ?? "";
                            
                            if (string.IsNullOrEmpty(partName) || partName == "Guid" || partType is Guid)
                            {
                                // Try to resolve name from GUID via KB
                                Guid pGuid = Guid.Empty;
                                if (partType is Guid g) pGuid = g;
                                else Guid.TryParse(partId, out pGuid);

                                if (pGuid != Guid.Empty)
                                {
                                    var model = GetKB().DesignModel;
                                    var pTypesProp = model.GetType().GetProperty("PartTypes");
                                    var pTypes = pTypesProp?.GetValue(model, null) as IEnumerable;
                                    if (pTypes != null)
                                    {
                                        foreach (object pt in pTypes)
                                        {
                                            var guidProp = pt.GetType().GetProperty("Guid") ?? pt.GetType().GetProperty("Id");
                                            var ptGuidValue = guidProp?.GetValue(pt, null);
                                            if (ptGuidValue != null && ptGuidValue.ToString().Equals(pGuid.ToString(), StringComparison.OrdinalIgnoreCase))
                                            {
                                                var nameProp = pt.GetType().GetProperty("Name") ?? pt.GetType().GetProperty("Description");
                                                partName = nameProp?.GetValue(pt, null) as string ?? "";
                                                break;
                                            }
                                        }
                                    }
                                }
                            }

                            if (string.IsNullOrEmpty(partName) || partName == "Guid" || partName == "PartType")
                            {
                                // Explicit fallback for Documentation part
                                if (partId.Equals("babf62c5-0111-49e9-a1c3-cc004d90900a", StringComparison.OrdinalIgnoreCase))
                                {
                                    partName = "Documentation";
                                }
                                else
                                {
                                    // Try description or just the type name
                                    partName = partType?.GetType().GetProperty("Description")?.GetValue(partType, null) as string ?? partType?.GetType().Name ?? "";
                                }
                            }

                            // Comprehensive content extraction
                            var pageProp = p.GetType().GetProperty("Page");
                            var sourceProp = p.GetType().GetProperty("Source");
                            var docProp = p.GetType().GetProperty("Documentation");
                            var contentProp = p.GetType().GetProperty("Content");
                            var editableContentProp = p.GetType().GetProperty("EditableContent");
                            var textProp = p.GetType().GetProperty("Text");
                            var templateProp = p.GetType().GetProperty("Template"); // WorkWithPlus Template
                            var elementProp = p.GetType().GetProperty("Element");

                            if (sourceProp != null)
                                innerXml = sourceProp.GetValue(p, null) as string ?? "";
                            
                            if (string.IsNullOrEmpty(innerXml) && docProp != null)
                                innerXml = docProp.GetValue(p, null) as string ?? "";

                            if (string.IsNullOrEmpty(innerXml) && contentProp != null)
                                innerXml = contentProp.GetValue(p, null) as string ?? "";

                            if (string.IsNullOrEmpty(innerXml) && editableContentProp != null)
                                innerXml = editableContentProp.GetValue(p, null) as string ?? "";

                            if (string.IsNullOrEmpty(innerXml) && textProp != null)
                                innerXml = textProp.GetValue(p, null) as string ?? "";

                            if (string.IsNullOrEmpty(innerXml) && templateProp != null)
                                innerXml = templateProp.GetValue(p, null) as string ?? "";

                            // Target 'Page' property specifically if it's a documentation/wiki part
                            if (string.IsNullOrEmpty(innerXml) && pageProp != null)
                            {
                                var page = pageProp.GetValue(p, null);
                                if (page != null)
                                {
                                    var pageContentProp = page.GetType().GetProperty("Content") ?? page.GetType().GetProperty("EditableContent") ?? page.GetType().GetProperty("Source");
                                    innerXml = pageContentProp?.GetValue(page, null) as string ?? "";
                                }
                            }

                            if (string.IsNullOrEmpty(innerXml) && elementProp != null)
                            {
                                var element = elementProp.GetValue(p, null);
                                var innerProp = element?.GetType().GetProperty("InnerXml");
                                innerXml = innerProp?.GetValue(element, null) as string ?? "";
                            }
                        }
                        catch { }

                        partsNode.Add(new XElement("Part",
                            new XAttribute("type", partId),
                            new XAttribute("name", partName),
                            new XElement("Source", innerXml)
                        ));
                    }
                }
                root.Add(partsNode);
                
                string xml = root.ToString();
                if (!string.IsNullOrEmpty(xml))
                {
                    AddToCache(target, xml, true);
                }
                return xml;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ObjectService] SDK Read Error: {ex.Message}");
                return null;
            }
        }

        public string ReadObject(string target)
        {
            try 
            {
                string xml = GetObjectXml(target);
                if (string.IsNullOrEmpty(xml))
                    return "{\"error\":\"Object not found: " + target + "\"}";

                return "{\"name\":\"" + target + "\", \"xml\":\"" + CommandDispatcher.EscapeJsonString(xml) + "\"}";
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ObjectService Read Error] {ex.Message}");
                return "{\"error\":\"SDK Read Error: " + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string GetPartSourceText(string target, string partName)
        {
            var obj = FindObject(target);
            if (obj == null) return null;

            foreach (var p in obj.Parts)
            {
                string pName = "";
                string pTypeGuid = "";

                try
                {
                    var typeProp = p.GetType().GetProperty("Type");
                    var partType = typeProp?.GetValue(p, null);
                    pTypeGuid = GetGuid(partType);
                    
                    var ptNameProp = partType?.GetType().GetProperty("Name");
                    pName = ptNameProp?.GetValue(partType, null) as string ?? "";

                    bool isMatch = false;
                    if (string.Equals(pName, partName, StringComparison.OrdinalIgnoreCase)) isMatch = true;

                    if (!isMatch && _partGuids.TryGetValue(partName, out Guid expectedGuid))
                    {
                        if (string.Equals(pTypeGuid, expectedGuid.ToString(), StringComparison.OrdinalIgnoreCase)) isMatch = true;
                    }

                    if (!isMatch && (partName.Equals("Documentation", StringComparison.OrdinalIgnoreCase) || partName.Equals("Doc", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (pTypeGuid.Equals("babf62c5-0111-49e9-a1c3-cc004d90900a", StringComparison.OrdinalIgnoreCase)) isMatch = true;
                    }

                    if (isMatch)
                    {
                        string source = "";
                        var sourceProp = p.GetType().GetProperty("Source");
                        var docProp = p.GetType().GetProperty("Documentation");
                        var contentProp = p.GetType().GetProperty("Content");
                        var editableContentProp = p.GetType().GetProperty("EditableContent");
                        var textProp = p.GetType().GetProperty("Text");
                        var templateProp = p.GetType().GetProperty("Template");
                        var elementProp = p.GetType().GetProperty("Element");
                        var pageProp = p.GetType().GetProperty("Page");

                        if (sourceProp != null) source = sourceProp.GetValue(p, null) as string ?? "";
                        if (string.IsNullOrEmpty(source) && docProp != null) source = docProp.GetValue(p, null) as string ?? "";
                        if (string.IsNullOrEmpty(source) && contentProp != null) source = contentProp.GetValue(p, null) as string ?? "";
                        if (string.IsNullOrEmpty(source) && editableContentProp != null) source = editableContentProp.GetValue(p, null) as string ?? "";
                        if (string.IsNullOrEmpty(source) && textProp != null) source = textProp.GetValue(p, null) as string ?? "";
                        if (string.IsNullOrEmpty(source) && templateProp != null) source = templateProp.GetValue(p, null) as string ?? "";

                        if (string.IsNullOrEmpty(source) && pageProp != null)
                        {
                            var page = pageProp.GetValue(p, null);
                            if (page != null)
                            {
                                var pageContentProp = page.GetType().GetProperty("Content") ?? page.GetType().GetProperty("EditableContent") ?? page.GetType().GetProperty("Source");
                                source = pageContentProp?.GetValue(page, null) as string ?? "";
                            }
                        }

                        if (string.IsNullOrEmpty(source) && elementProp != null)
                        {
                            var element = elementProp.GetValue(p, null);
                            var innerProp = element?.GetType().GetProperty("InnerXml");
                            source = innerProp?.GetValue(element, null) as string ?? "";
                        }
                        return source;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ObjectService] Error reading part {pName}: {ex.Message}");
                }
            }
            return null;
        }

        public string ReadObjectSource(string target, string partName)
        {
            try
            {
                string source = GetPartSourceText(target, partName);
                if (source == null) return "{\"error\":\"Object or Part not found\"}";
                return "{\"name\":\"" + target + "\", \"part\":\"" + partName + "\", \"source\":\"" + CommandDispatcher.EscapeJsonString(source) + "\"}";
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string ReadObjectSection(string target, string partName, string sectionName)
        {
            try
            {
                string partCode = GetPartSourceText(target, partName);
                if (partCode == null) return "{\"error\":\"Object or Part not found\"}";

                var range = CodeParser.GetSectionRange(partCode, sectionName);
                if (range.start == -1) return "{\"error\":\"Section not found: " + sectionName + "\"}";

                string sectionContent = partCode.Substring(range.start, range.end - range.start);
                return "{\"name\":\"" + target + "\", \"part\":\"" + partName + "\", \"section\":\"" + sectionName + "\", \"source\":\"" + CommandDispatcher.EscapeJsonString(sectionContent) + "\"}";
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string GetVariables(string target)
        {
            try
            {
                var obj = FindObject(target);
                if (obj == null) return "{\"error\":\"Object not found: " + target + "\"}";

                var varsPart = obj.Parts.Get<VariablesPart>();
                if (varsPart == null) return "{\"name\":\"" + target + "\", \"variables\":[]}";

                var variables = new List<object>();
                foreach (Variable v in varsPart.Variables)
                {
                    var vInfo = new JObject();
                    vInfo["name"] = v.Name;
                    vInfo["description"] = v.Description;
                    vInfo["type"] = v.Type.ToString();
                    vInfo["length"] = v.Length;
                    vInfo["decimals"] = v.Decimals;
                    vInfo["isCollection"] = v.IsCollection;
                    
                    if (v.Type == eDBType.GX_SDT)
                    {
                        try {
                            // Use reflection to get TypeName or similar if property not direct
                            var typeNameProp = v.GetType().GetProperty("TypeName");
                            vInfo["typeName"] = typeNameProp?.GetValue(v, null)?.ToString() ?? "";
                        } catch {
                            vInfo["typeName"] = "Unknown SDT";
                        }
                    }
                    
                    variables.Add(vInfo);
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new { 
                    name = target, 
                    variables = variables 
                });
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string GetAttributeMetadata(string name)
        {
            try
            {
                var kb = EnsureKbOpen();
                string namePart = name.Contains(":") ? name.Split(':')[1] : name;
                var att = Attribute.Get(kb.DesignModel, namePart);
                if (att == null) return "{\"error\":\"Attribute not found: " + namePart + "\"}";

                var result = new JObject();
                result["name"] = att.Name;
                result["description"] = att.Description;
                result["type"] = att.Type.ToString();
                result["length"] = att.Length;
                result["decimals"] = att.Decimals;
                result["isFormula"] = att.Formula != null;
                
                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public void Invalidate(string target)
        {
            if (string.IsNullOrEmpty(target)) return;
            if (_cache.ContainsKey(target))
            {
                _cache.Remove(target);
                _lru.Remove(target);
            }

            // Invalidate disk cache too
            string safeName = target.Replace(":", "_");
            string cachePath = Path.Combine(_diskCacheDir, safeName + ".xml");
            if (File.Exists(cachePath))
            {
                try { File.Delete(cachePath); } catch { }
            }
            Console.Error.WriteLine($"[ObjectService] Cache invalidated for: {target} (L1+L2)");
        }

        public string ParseGenerusXmlToJson(string xml)
        {
            try 
            {
                var doc = XDocument.Parse(xml);
                var objNode = doc.Descendants("Object").FirstOrDefault();
                if (objNode == null) return "{\"error\":\"Invalid GeneXus XML\"}";

                var result = new JObject();
                result["name"] = objNode.Attribute("name")?.Value;
                result["guid"] = objNode.Attribute("guid")?.Value;
                result["type"] = objNode.Attribute("type")?.Value;
                result["status"] = "Success";

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"JSON Parse Error: " + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private void AddToCache(string key, string value, bool saveToDisk = true)
        {
            if (_cache.Count >= MAX_CACHE_SIZE)
            {
                string oldest = _lru[0];
                _cache.Remove(oldest);
                _lru.RemoveAt(0);
            }

            _cache[key] = value;
            _lru.Add(key);

            if (saveToDisk)
            {
                try {
                    string safeName = key.Replace(":", "_");
                    File.WriteAllText(Path.Combine(_diskCacheDir, safeName + ".xml"), value);
                } catch { }
            }
        }
    }
}
