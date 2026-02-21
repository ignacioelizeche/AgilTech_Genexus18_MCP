using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using System.Diagnostics;
using System.Reflection;

namespace GxMcp.Worker.Services
{
    public class ObjectService
    {
        private readonly KbService _kbService;
        private readonly IndexCacheService _indexCache;
        private DataInsightService _dataInsightService;
        private UIService _uiService;

        // Guaranteed GUIDs for GX18 (Empirically validated in this KB)
        private static readonly Guid PART_PROCEDURE = new Guid("528d1c06-a9c2-420d-bd35-21dca83f12ff");
        private static readonly Guid PART_RULES     = new Guid("9b0a32a3-de6d-4be1-a4dd-1b85d3741534");
        private static readonly Guid PART_EVENTS    = new Guid("c44bd5ff-f918-415b-98e6-aca44fed84fa");
        private static readonly Guid PART_VARIABLES = new Guid("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
        private static readonly Guid PART_WEB_FORM  = new Guid("d24a58ad-57ba-41b7-9e6e-eaca3543c778");
        private static readonly Guid PART_STRUCTURE = new Guid("264be5fb-1b28-4b25-a598-6ca900dd059f");
        private static readonly Guid PART_WWP_INSTANCE = new Guid("d8074ef1-17d8-0000-0002-b49c0f0c02f3");

        // Object Class GUIDs
        private static readonly Guid OBJ_TRANSACTION = new Guid("1db606f2-af09-4cf9-a3b5-b481519d28f6");
        private static readonly Guid OBJ_PROCEDURE   = new Guid("8e0f6990-23e5-0000-0005-55ff16766446");
        private static readonly Guid OBJ_WEBPANEL    = new Guid("c9584656-94b6-4ccd-890f-332d11fc2c25");
        private static readonly Guid OBJ_TABLE        = new Guid("857ca50e-7905-0000-0007-c5d9ff2975ec");
        private static readonly Guid OBJ_ATTRIBUTE    = new Guid("adbb33c9-0906-4971-833c-998de27e0676");

        public ObjectService(KbService kbService, BuildService buildService)
        {
            _kbService = kbService;
            _indexCache = new IndexCacheService(buildService);
        }

        public void SetDataInsightService(DataInsightService service)
        {
            _dataInsightService = service;
        }

        public void SetUIService(UIService service)
        {
            _uiService = service;
        }

        public global::Artech.Architecture.Common.Objects.KnowledgeBase GetKB() { return _kbService.GetKB(); }

        public SearchIndex GetIndex()
        {
            return _indexCache.GetIndex();
        }

        public global::Artech.Architecture.Common.Objects.KBObject FindObject(string target)
        {
            var sw = Stopwatch.StartNew();
            try {
                var kb = GetKB();
                if (kb == null) return null;

                string namePart = target;
                Guid? expectedType = null;

                if (target.Contains(":"))
                {
                    var parts = target.Split(':');
                    string prefix = parts[0].ToLower();
                    namePart = parts[1];

                    if (prefix == "trn" || prefix == "transaction") expectedType = OBJ_TRANSACTION;
                    else if (prefix == "prc" || prefix == "procedure") expectedType = OBJ_PROCEDURE;
                    else if (prefix == "wp" || prefix == "webpanel") expectedType = OBJ_WEBPANEL;
                    else if (prefix == "tbl" || prefix == "table") expectedType = OBJ_TABLE;
                    else if (prefix == "att" || prefix == "attribute") expectedType = OBJ_ATTRIBUTE;
                }
                
                // PERFORMANCE BOOST: Use our Index Cache to find the Type first!
                var index = _indexCache.GetIndex();
                if (expectedType == null && index != null)
                {
                    var entry = index.Objects.Values.FirstOrDefault(o => string.Equals(o.Name, namePart, StringComparison.OrdinalIgnoreCase));
                    if (entry != null) {
                        Logger.Debug($"Index found '{namePart}' as {entry.Type}");
                    }
                }

                // GX18 Signature: GetByName(string namespace, Guid? type, string name)
                var results = kb.DesignModel.Objects.GetByName(null, expectedType, namePart);
                foreach (global::Artech.Architecture.Common.Objects.KBObject obj in results)
                {
                    if (string.Equals(obj.Name, namePart, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Debug($"FindObject '{target}' SUCCESS in {sw.ElapsedMilliseconds}ms");
                        return obj;
                    }
                }
            } catch (Exception ex) { Logger.Error("FindObject Fatal: " + ex.Message); }
            return null;
        }

        public string ReadObjectSource(string target, string partName)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var obj = FindObject(target);
                if (obj == null) return "{\"error\": \"Object not found\"}";

                Guid partGuid = MapLogicalPartToGuid(obj.TypeDescriptor.Name, partName);
                
                global::Artech.Architecture.Common.Objects.KBObjectPart part = null;
                // SPEED: Manual loop on Parts collection (already loaded by FindObject)
                foreach (global::Artech.Architecture.Common.Objects.KBObjectPart p in obj.Parts)
                {
                    if (p.Type == partGuid) { part = p; break; }
                }

                if (part == null) return "{\"error\": \"Part '" + partName + "' not found.\"}";

                JObject result;
                if (part is global::Artech.Architecture.Common.Objects.ISource sourcePart)
                {
                    string content = sourcePart.Source;
                    Logger.Info($"ReadSource (Text) SUCCESS in {sw.ElapsedMilliseconds}ms");
                    result = new JObject { ["source"] = content };
                }
                else
                {
                    // Phase 5: XML Part Handling
                    string xml = part.SerializeToXml();
                    Logger.Info($"ReadSource (XML) SUCCESS in {sw.ElapsedMilliseconds}ms");
                    result = new JObject { ["xml"] = xml };
                }

                // Holistic Data Vision Integration
                if (_dataInsightService != null)
                {
                    try
                    {
                        string contextJson = _dataInsightService.GetDataContext(target);
                        result["dataContext"] = JToken.Parse(contextJson);
                    }
                    catch (Exception ex) { Logger.Warn($"Failed to attach data context: {ex.Message}"); }
                }

                // UI Vision Integration
                if (_uiService != null)
                {
                    try
                    {
                        string uiJson = _uiService.GetUIContext(target);
                        result["uiContext"] = JToken.Parse(uiJson);
                    }
                    catch (Exception ex) { Logger.Warn($"Failed to attach UI context: {ex.Message}"); }
                }

                return result.ToString();
            }
            catch (Exception ex) { 
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}"; 
            }
        }

        private Guid MapLogicalPartToGuid(string objType, string logicalPart)
        {
            string lp = logicalPart.ToLower();
            if (lp == "source" || lp == "code") return (objType == "Procedure") ? PART_PROCEDURE : PART_EVENTS;
            if (lp == "rules") return PART_RULES;
            if (lp == "events") return PART_EVENTS;
            if (lp == "variables") return PART_VARIABLES;
            if (lp == "webform" || lp == "form" || lp == "layout") return PART_WEB_FORM;
            if (lp == "structure") return PART_STRUCTURE;
            if (lp == "wwp" || lp == "instance" || lp == "pattern") return PART_WWP_INSTANCE;
            return PART_PROCEDURE;
        }

        public string ReadObject(string target)
        {
            var obj = FindObject(target);
            if (obj == null) return "{\"error\": \"Object not found\"}";
            
            var parts = new JArray();
            foreach (global::Artech.Architecture.Common.Objects.KBObjectPart p in obj.Parts)
            {
                parts.Add(new JObject {
                    ["typeGuid"] = p.Type.ToString(),
                    ["typeName"] = p.TypeDescriptor.Name
                });
            }

            return new JObject { 
                ["name"] = obj.Name, 
                ["type"] = obj.TypeDescriptor.Name,
                ["parts"] = parts
            }.ToString();
        }

        public string GetObjectXml(string target)
        {
            var obj = FindObject(target);
            if (obj == null) return null;
            return obj.SerializeToXml();
        }

        public string GetObjectSource(string target)
        {
            var obj = FindObject(target);
            if (obj == null) return null;

            string code = "";
            foreach (global::Artech.Architecture.Common.Objects.KBObjectPart part in obj.Parts)
            {
                if (part is global::Artech.Architecture.Common.Objects.ISource sourcePart)
                {
                    code += sourcePart.Source + "\n";
                }
            }
            return code;
        }
    }
}
