using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class PatternAnalysisService
    {
        private static readonly Guid PatternInstancePartGuid = new Guid("a51ced48-7bee-0001-ab12-04e9e32123d1");
        private readonly ObjectService _objectService;

        public PatternAnalysisService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        public string GetWWPStructure(string target)
        {
            try
            {
                var obj = _objectService.FindObject(target);
                if (obj == null) return Models.McpResponse.Error("Object not found", target, null, "The requested object is not available in the active Knowledge Base.");

                KBObject instanceObj = ResolveWWPInstance(obj);
                if (instanceObj == null) return Models.McpResponse.Error("WorkWithPlus instance not found", target, null, "No WorkWithPlus instance was resolved for the requested object.");

                var part = FindPatternPart(instanceObj, "PatternInstance");
                if (part == null) return Models.McpResponse.Error("PatternInstance part not found", target, "PatternInstance", "The WorkWithPlus instance does not expose a PatternInstance part.", instanceObj.Name, instanceObj.TypeDescriptor?.Name, new JArray(GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(instanceObj)));

                string xml = ExtractEditablePatternXml(part, instanceObj);
                if (string.IsNullOrEmpty(xml)) return Models.McpResponse.Error("PatternInstance XML not available", target, "PatternInstance", "The PatternInstance content could not be extracted from the resolved WorkWithPlus instance.", instanceObj.Name, instanceObj.TypeDescriptor?.Name);

                var result = ParseWWPXml(xml);
                result["resolvedObject"] = instanceObj.Name;
                result["resolvedType"] = instanceObj.TypeDescriptor?.Name;
                result["rawSnippet"] = xml.Length > 5000 ? xml.Substring(0, 5000) : xml;
                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public static bool IsPatternPart(string partName)
        {
            return string.Equals(partName, "PatternInstance", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(partName, "PatternVirtual", StringComparison.OrdinalIgnoreCase);
        }

        public KBObject ResolveWWPInstance(KBObject obj)
        {
            if (obj == null) return null;
            if (obj.TypeDescriptor.Name.Equals("WorkWithPlus", StringComparison.OrdinalIgnoreCase)) return obj;

            var model = obj.Model;
            if (model == null) return null;

            string instanceName = "WorkWithPlus" + obj.Name;
            var namedMatch = model.Objects.GetAll()
                .FirstOrDefault(o =>
                    o.TypeDescriptor.Name.Equals("WorkWithPlus", StringComparison.OrdinalIgnoreCase) &&
                    o.Name.Equals(instanceName, StringComparison.OrdinalIgnoreCase));
            if (namedMatch != null) return namedMatch;

            try
            {
                var childMatch = model.Objects.GetChildren(obj)
                    .FirstOrDefault(o => o.TypeDescriptor.Name.Equals("WorkWithPlus", StringComparison.OrdinalIgnoreCase));
                if (childMatch != null) return childMatch;
            }
            catch
            {
            }

            return null;
        }

        public KBObjectPart FindPatternPart(KBObject instanceObj, string partName)
        {
            if (instanceObj == null || string.IsNullOrWhiteSpace(partName)) return null;

            return instanceObj.Parts.Cast<KBObjectPart>().FirstOrDefault(p =>
            {
                if (string.Equals(partName, "PatternInstance", StringComparison.OrdinalIgnoreCase))
                {
                    return p.Name.Equals("PatternInstance", StringComparison.OrdinalIgnoreCase) ||
                           p.GetType().Name.Contains("PatternInstance") ||
                           p.Type.Equals(PatternInstancePartGuid);
                }

                return p.Name.Equals(partName, StringComparison.OrdinalIgnoreCase) ||
                       p.GetType().Name.IndexOf(partName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       string.Equals(p.TypeDescriptor?.Name, partName, StringComparison.OrdinalIgnoreCase);
            });
        }

        public string ReadPatternPartXml(KBObject obj, string partName, out KBObject resolvedObject, out string resolvedPartName)
        {
            resolvedObject = ResolveWWPInstance(obj);
            resolvedPartName = partName;
            if (resolvedObject == null) return null;

            var part = FindPatternPart(resolvedObject, partName);
            if (part == null) return null;

            resolvedPartName = !string.IsNullOrWhiteSpace(part.Name) ? part.Name : partName;
            return ExtractEditablePatternXml(part, resolvedObject);
        }

        public string BuildPatternPartEnvelope(KBObject obj, string partName, string innerXml, out KBObject resolvedObject, out KBObjectPart resolvedPart)
        {
            resolvedObject = ResolveWWPInstance(obj);
            resolvedPart = null;
            if (resolvedObject == null) return null;

            resolvedPart = FindPatternPart(resolvedObject, partName);
            if (resolvedPart == null) return null;

            string partXml = SerializeEditablePatternEnvelope(resolvedObject, resolvedPart);
            if (string.IsNullOrWhiteSpace(partXml)) return null;

            try
            {
                var outer = XDocument.Parse(partXml, LoadOptions.PreserveWhitespace);
                var dataElement = outer.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("Data", StringComparison.OrdinalIgnoreCase));
                if (dataElement == null) return null;

                dataElement.ReplaceNodes(new XCData(innerXml));
                return outer.ToString(SaveOptions.DisableFormatting);
            }
            catch
            {
                return null;
            }
        }

        private KBObject FindWWPInstance(Transaction trn)
        {
            var model = trn.Model;
            
            // Search by name using SDK compliant way
            // In GX SDK, names are resolved via ResolveName or looking into the collection
            var instance = model.Objects.GetAll()
                                .FirstOrDefault(o => o.Name.Equals("WorkWithPlus" + trn.Name, StringComparison.OrdinalIgnoreCase));
            
            if (instance != null) return instance;

            // Search by children
            return model.Objects.GetChildren(trn)
                        .FirstOrDefault(o => o.TypeDescriptor.Name.Equals("WorkWithPlus", StringComparison.OrdinalIgnoreCase));
        }

        private string ExtractEditablePatternXml(KBObjectPart part, KBObject instanceObj)
        {
            string xml = TryReadPatternInnerXml(part);
            if (!string.IsNullOrEmpty(xml) && !LooksLikePartPropertiesOnly(xml)) return xml;

            try
            {
                return ExtractInnerXmlFromSerializedFragment(SerializeEditablePatternEnvelope(instanceObj, part));
            }
            catch
            {
                return null;
            }
        }

        private string TryReadPatternInnerXml(KBObjectPart part)
        {
            if (part == null) return null;

            if (part is ISource sourcePart && !string.IsNullOrWhiteSpace(sourcePart.Source))
            {
                return NormalizeXml(sourcePart.Source);
            }

            try
            {
                dynamic dPart = part;
                string[] propertyNames = { "InstanceXml", "Specification", "Settings" };
                foreach (string propertyName in propertyNames)
                {
                    try
                    {
                        string candidate = dPart.Properties.Get<string>(propertyName);
                        if (!string.IsNullOrWhiteSpace(candidate))
                        {
                            return NormalizeXml(candidate);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return ExtractInnerXmlFromSerializedFragment(SerializePatternPart(part));
        }

        public string ExtractEditablePatternXmlForDiagnostics(KBObjectPart part)
        {
            return ExtractInnerXmlFromSerializedFragment(SerializePatternPart(part));
        }

        private string SerializePatternPart(KBObjectPart part)
        {
            try
            {
                return part.SerializeToXml();
            }
            catch
            {
                return null;
            }
        }

        private string SerializeEditablePatternEnvelope(KBObject instanceObj, KBObjectPart part)
        {
            if (instanceObj == null || part == null) return null;

            try
            {
                using (var writer = new System.IO.StringWriter())
                {
                    instanceObj.Serialize(writer);
                    string objectXml = writer.ToString();
                    var doc = XDocument.Parse(objectXml, LoadOptions.PreserveWhitespace);
                    var partElement = doc.Descendants().FirstOrDefault(e =>
                        e.Name.LocalName.Equals("Part", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals((string)e.Attribute("type"), part.Type.ToString(), StringComparison.OrdinalIgnoreCase));
                    return partElement?.ToString(SaveOptions.DisableFormatting);
                }
            }
            catch
            {
                return SerializePatternPart(part);
            }
        }

        private string ExtractInnerXmlFromSerializedFragment(string serializedXml)
        {
            if (string.IsNullOrWhiteSpace(serializedXml)) return null;

            try
            {
                var outer = XDocument.Parse(serializedXml, LoadOptions.PreserveWhitespace);
                var dataElement = outer.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("Data", StringComparison.OrdinalIgnoreCase));
                if (dataElement != null && !string.IsNullOrWhiteSpace(dataElement.Value))
                {
                    return NormalizeXml(dataElement.Value);
                }
            }
            catch
            {
            }

            return NormalizeXml(serializedXml);
        }

        private string NormalizeXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return xml;

            try
            {
                return XDocument.Parse(xml, LoadOptions.PreserveWhitespace).ToString();
            }
            catch
            {
                return xml;
            }
        }

        private bool LooksLikePartPropertiesOnly(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return false;

            try
            {
                var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                return doc.Root != null &&
                       doc.Root.Name.LocalName.Equals("Properties", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private JObject ParseWWPXml(string xml)
        {
            var result = new JObject();
            try
            {
                string realXml = xml;
                if (xml.Contains("<![CDATA["))
                {
                    int start = xml.IndexOf("<![CDATA[") + 9;
                    int end = xml.LastIndexOf("]]>");
                    if (end > start)
                    {
                        realXml = xml.Substring(start, end - start);
                    }
                }

                XDocument doc = XDocument.Parse(realXml);
                var root = doc.Root;
                
                result["template"] = root?.Attribute("template")?.Value ?? root?.Attribute("Template")?.Value;
                
                var attributes = new JArray();
                foreach (var att in doc.Descendants().Where(e => e.Name.LocalName.Equals("attribute", StringComparison.OrdinalIgnoreCase)))
                {
                    var aObj = new JObject();
                    string rawAtt = att.Attribute("attribute")?.Value ?? "";
                    // WWP format is often GUID-AttributeName
                    string name = rawAtt.Contains("-") ? rawAtt.Substring(rawAtt.LastIndexOf("-") + 1) : rawAtt;
                    
                    aObj["name"] = name;
                    aObj["description"] = att.Attribute("description")?.Value;
                    aObj["visible"] = att.Attribute("visible")?.Value;
                    aObj["readOnly"] = att.Attribute("readOnly")?.Value;
                    attributes.Add(aObj);
                }
                result["attributes"] = attributes;

                var variables = new JArray();
                foreach (var varNode in doc.Descendants().Where(e => e.Name.LocalName.Equals("variable", StringComparison.OrdinalIgnoreCase)))
                {
                    var vObj = new JObject();
                    vObj["name"] = varNode.Attribute("name")?.Value ?? varNode.Attribute("Name")?.Value;
                    vObj["description"] = varNode.Attribute("description")?.Value;
                    vObj["readOnly"] = varNode.Attribute("readOnly")?.Value;
                    variables.Add(vObj);
                }
                result["variables"] = variables;

                var actions = new JArray();
                foreach (var act in doc.Descendants().Where(e => e.Name.LocalName.Contains("Action")))
                {
                    var actObj = new JObject();
                    actObj["name"] = act.Attribute("name")?.Value ?? act.Attribute("Name")?.Value;
                    actObj["caption"] = act.Attribute("caption")?.Value ?? act.Attribute("Caption")?.Value;
                    actions.Add(actObj);
                }
                result["actions"] = actions;

                var tabs = new JArray();
                foreach (var tab in doc.Descendants().Where(e => e.Name.LocalName.Equals("tab", StringComparison.OrdinalIgnoreCase)))
                {
                    var tObj = new JObject();
                    tObj["name"] = tab.Attribute("Name")?.Value ?? tab.Attribute("caption")?.Value;
                    tObj["caption"] = tab.Attribute("caption")?.Value;
                    tabs.Add(tObj);
                }
                result["tabs"] = tabs;

                var grids = new JArray();
                foreach (var grid in doc.Descendants().Where(e => e.Name.LocalName.Equals("grid", StringComparison.OrdinalIgnoreCase)))
                {
                    var gObj = new JObject();
                    gObj["name"] = grid.Attribute("name")?.Value ?? grid.Attribute("Name")?.Value;
                    grids.Add(gObj);
                }
                result["grids"] = grids;
            }
            catch (Exception ex)
            {
                result["parsingError"] = ex.Message;
            }
            return result;
        }
    }
}
