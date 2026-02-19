using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Collections;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Packages;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class WriteService
    {
        private readonly ObjectService _objectService;
        private readonly BuildService _buildService;
        private readonly KbService _kbService;
        private readonly AnalyzeService _analyzeService;

        public WriteService(ObjectService objectService, BuildService buildService, KbService kbService, AnalyzeService analyzeService)
        {
            _objectService = objectService;
            _buildService = buildService;
            _kbService = kbService;
            _analyzeService = analyzeService;
        }

        public string WriteObject(string target, string partName, string newCode)
        {
            try
            {
                Logger.Info($"[WriteService] Native write to: {target} (Part: {partName})");
                
                var kb = _kbService.GetKB();
                string namePart = target.Contains(":") ? target.Split(':')[1] : target;

                KBObject obj = FindObject(kb, target);
                if (obj == null) return "{\"error\": \"Object not found: " + target + "\"}";

                // Robust TypeGuid retrieval
                string objTypeGuid = "";
                var typeDescProp = obj.GetType().GetProperty("TypeDescriptor") ?? obj.GetType().GetProperty("Type");
                var typeDesc = typeDescProp?.GetValue(obj, null);
                if (typeDesc != null) objTypeGuid = GetGuid(typeDesc);
                if (string.IsNullOrEmpty(objTypeGuid)) objTypeGuid = GetGuid(obj); // Fallback to obj directly

                Logger.Info($"[WriteService] Target: {target}, Name: {obj.Name}, Type: {objTypeGuid}, Requested Part: {partName}");

                // Aggressive Part Logging
                Logger.Info($"[WriteService] Scanning {obj.Parts.Count} parts for {obj.Name}:");
                foreach (KBObjectPart p in obj.Parts)
                {
                    string pTypeGuid = "";
                    string pTypeName = "";
                    try {
                        var pTypeProp = p.GetType().GetProperty("Type");
                        var pTypeObj = pTypeProp?.GetValue(p, null);
                        pTypeGuid = GetGuid(pTypeObj);
                        var pNameProp = pTypeObj?.GetType().GetProperty("Name");
                        pTypeName = pNameProp?.GetValue(pTypeObj, null) as string;
                    } catch {}
                    Logger.Info($"  - Part: '{pTypeName}' (Guid: {pTypeGuid}) Type: {p.GetType().Name}");
                }

                // Force Conversion Hack: If it's a Win Trn (857ca50e...) and we want Events, try to upgrade to Web Trn (1db606f2...)
                if (partName.ToLower() == "events" && objTypeGuid.Equals("857ca50e-7905-0000-0007-c5d9ff2975ec", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Info("[WriteService] Detected Win Transaction. Attempting to force-upgrade to Web Transaction to enable Events.");
                    try {
                        var tGuidProp = obj.GetType().GetProperty("TypeGuid");
                        if (tGuidProp != null) {
                            tGuidProp.SetValue(obj, new Guid("1db606f2-af09-4cf9-a3b5-b481519d28f6"), null);
                            Logger.Info("[WriteService] TypeGuid updated in memory. Saving object...");
                            
                            // Explicit Save to persist the type change
                            var saveMethod = obj.GetType().GetMethod("Save", new Type[0]);
                            if (saveMethod != null) {
                                saveMethod.Invoke(obj, null);
                                Logger.Info("[WriteService] Object saved successfully as Web Transaction.");
                                // Update local state for subsequent logic
                                objTypeGuid = "1db606f2-af09-4cf9-a3b5-b481519d28f6";
                            }
                        }
                    } catch (Exception ex) {
                        Logger.Error($"[WriteService] Failed to force-upgrade type: {ex.Message}");
                    }
                }

                Guid partType = GetPartTypeGuid(partName);
                if (partType == Guid.Empty)
                {
                    partType = GetPartTypeByDynamicName(kb, partName);
                    if (partType != Guid.Empty) Logger.Info($"[WriteService] Found part '{partName}' GUID via dynamic lookup: {partType}");
                }

                KBObjectPart part = null;

                if (part == null)
                {
                    foreach (KBObjectPart p in obj.Parts)
                    {
                        var typeProp = p.GetType().GetProperty("Type");
                        var pType = typeProp?.GetValue(p, null);
                        if (pType == null) continue;

                        string partId = GetGuid(pType);
                        
                        var nameProp = pType.GetType().GetProperty("Name");
                        string name = nameProp?.GetValue(pType, null) as string;

                        if (string.Equals(name, partName, StringComparison.OrdinalIgnoreCase))
                        {
                            part = p;
                        }

                        // Special case for Transaction Rules
                        if (partName.ToLower() == "rules" && (partId == "00000000-0000-0000-0002-000000000004" || partId.Equals("00000000-0000-0000-0002-000000000004", StringComparison.OrdinalIgnoreCase)))
                        {
                            part = p;
                        }
                        
                        // Special case for Transaction Events (Web)
                        if (partName.ToLower() == "events" && (partId == "c44bd5ff-f918-415b-98e6-aca44fed84fa" || partId.Equals("c44bd5ff-f918-415b-98e6-aca44fed84fa", StringComparison.OrdinalIgnoreCase)))
                        {
                            part = p;
                        }
                    }
                }

                if (part == null)
                {
                    Guid partGuid = Guid.Empty;
                    string lowerPartName = partName.ToLower();
                    
                    if (lowerPartName == "rules") partGuid = new Guid("9b0a32a3-de6d-4be1-a4dd-1b85d3741534"); // Standard Rules
                    else if (lowerPartName == "events") partGuid = new Guid("c414ed00-8cc4-4f44-8820-4baf93547173"); // Standard Events
                    else partGuid = GetPartTypeGuid(partName);

                    // Try standard creation/get patterns
                    part = TryCreatePart(kb, obj, partGuid);

                    // Second fallback for Transactions specifically if standard IDs didn't work
                    if (part == null)
                    {
                        if (lowerPartName == "rules") partGuid = new Guid("00000000-0000-0000-0002-000000000004");
                        else if (lowerPartName == "events") partGuid = new Guid("c44bd5ff-f918-415b-98e6-aca44fed84fa");

                        if (partGuid != Guid.Empty)
                        {
                            part = TryCreatePart(kb, obj, partGuid);
                        }
                    }

                // Specialized Transaction logic for Events
                if (partName.ToLower() == "events" && objTypeGuid.Equals("1db606f2-af09-4cf9-a3b5-b481519d28f6", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Info("[WriteService] Attempting specialized Transaction.WebEvents property access...");
                    try {
                        var webEventsProp = obj.GetType().GetProperty("WebEvents");
                        if (webEventsProp != null) {
                            part = webEventsProp.GetValue(obj, null) as KBObjectPart;
                            if (part != null) Logger.Info("[WriteService] Specialized access succeeded via WebEvents property.");
                        }
                    } catch (Exception ex) {
                        Logger.Info($"[WriteService] Specialized access failed: {ex.Message}");
                    }
                }

                if (part == null)
                {
                    part = TryCreatePart(kb, obj, partType);
                }
                    if (part != null) Logger.Info($"[WriteService] Part '{partName}' ensured successfully.");
                }

                if (part == null) return "{\"error\": \"Part not found: " + partName + "\"}";

                // 1. Try setting 'Source' property (Standard for ProcedurePart, EventsPart, etc.)
                var sourceProp = part.GetType().GetProperty("Source", BindingFlags.Public | BindingFlags.Instance);
                if (sourceProp != null && sourceProp.CanWrite)
                {
                    sourceProp.SetValue(part, newCode, null);
                    Logger.Info("[WriteService] Updated via Part.Source property.");
                }
                else
                {
                    // 2. Fallback to Element.InnerXml (for some specific parts)
                    var elementProp = part.GetType().GetProperty("Element");
                    var element = elementProp?.GetValue(part, null);
                    if (element != null)
                    {
                        var innerXmlProp = element.GetType().GetProperty("InnerXml");
                        innerXmlProp?.SetValue(element, newCode, null);
                        Logger.Info("[WriteService] Updated via Element.InnerXml.");
                    }
                }

                // 3. Special Case: WorkWithPlus Source
                // If WWP is present, it often uses a custom part. Let's look for any part that might be WWP.
                foreach (KBObjectPart p in obj.Parts)
                {
                    string typeName = p.GetType().Name;
                    if (typeName.Contains("WorkWithPlus") || typeName.Contains("DVelop"))
                    {
                        var wwpSourceProp = p.GetType().GetProperty("Source");
                        wwpSourceProp?.SetValue(p, newCode, null);
                        Logger.Info($"[WriteService] Also updated suspected WWP part: {typeName}");
                    }
                }

                obj.Save();
                _objectService.Invalidate(target);

                // Live Indexing: Trigger immediate analysis
                try {
                    Logger.Info($"[WriteService] Triggering Live Indexing for {target}...");
                    _analyzeService.AnalyzeInternal(target);
                } catch (Exception ex) {
                    Logger.Error($"[WriteService] Live Indexing Failed: {ex.Message}");
                }

                return "{\"status\": \"Success\", \"object\": \"" + target + "\"}";
            }
            catch (Exception ex)
            {
                Logger.Error($"[WriteService Error] {ex.Message}");
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private KBObject FindObject(KnowledgeBase kb, string target)
        {
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

            foreach (KBObject kbo in kb.DesignModel.Objects)
            {
                if (string.Equals(kbo.Name, namePart, StringComparison.OrdinalIgnoreCase))
                {
                    if (expectedType == null || kbo.Type == expectedType.Value)
                    {
                        return kbo;
                    }
                }
            }
            return null;
        }

        private Guid GetPartTypeByDynamicName(KnowledgeBase kb, string partName)
        {
            var model = kb.DesignModel;
            // Try to find a property that contains PartTypes
            var partTypesProp = model.GetType().GetProperty("PartTypes");
            var partTypes = partTypesProp?.GetValue(model, null) as IEnumerable;
            
            if (partTypes != null)
            {
                foreach (object pt in partTypes)
                {
                    try {
                        var nameProp = pt.GetType().GetProperty("Name");
                        string ptName = nameProp?.GetValue(pt, null) as string;
                        if (string.Equals(ptName, partName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(ptName, "Web" + partName, StringComparison.OrdinalIgnoreCase) ||
                            (partName.ToLower() == "events" && ptName.ToLower() == "webevents"))
                        {
                             var guidProp = pt.GetType().GetProperty("Guid");
                             return (Guid)guidProp.GetValue(pt, null);
                        }
                    } catch {}
                }
            }
            return Guid.Empty;
        }

        private KBObjectPart TryCreatePart(KnowledgeBase kb, KBObject obj, Guid partGuid)
        {
            if (partGuid == Guid.Empty) return null;

            try
            {
                // Stage 1: Try Parts.LasyGet(Guid)
                var lasyGetMethod = obj.Parts.GetType().GetMethod("LasyGet", new Type[] { typeof(Guid) });
                if (lasyGetMethod != null)
                {
                    return lasyGetMethod.Invoke(obj.Parts, new object[] { partGuid }) as KBObjectPart;
                }

                // Stage 2: Try Parts.Get(Guid, true)
                var getMethod = obj.Parts.GetType().GetMethod("Get", new Type[] { typeof(Guid), typeof(bool) });
                if (getMethod != null)
                {
                    return getMethod.Invoke(obj.Parts, new object[] { partGuid, true }) as KBObjectPart;
                }

                // Stage 3: Try Parts.Create(Guid)
                var createMethod = obj.Parts.GetType().GetMethod("Create", new Type[] { typeof(Guid) });
                if (createMethod != null)
                {
                    return createMethod.Invoke(obj.Parts, new object[] { partGuid }) as KBObjectPart;
                }

                // Stage 4: Explicit PartType lookup and obj.Parts.Add(PartType)
                Logger.Info($"[WriteService] Stage 4: Explicit PartType lookup for {partGuid}");
                var model = kb.DesignModel;
                var partTypesProp = model.GetType().GetProperty("PartTypes");
                var partTypes = partTypesProp?.GetValue(model, null) as IEnumerable;
                object ptObj = null;
                if (partTypes != null) {
                    foreach (object pt in partTypes) {
                        try {
                             var gProp = pt.GetType().GetProperty("Guid");
                             if ((Guid)gProp.GetValue(pt, null) == partGuid) {
                                 ptObj = pt;
                                 break;
                             }
                        } catch {}
                    }
                }

                if (ptObj != null) {
                    Logger.Info($"[WriteService] Found PartType object. Attempting obj.Parts.Add(pt)");
                    // Look for Add method that takes one object (the PartType)
                    var addMethod = obj.Parts.GetType().GetMethods().FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1);
                    if (addMethod != null) {
                        return addMethod.Invoke(obj.Parts, new object[] { ptObj }) as KBObjectPart;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[WriteService] Create error for {partGuid}: {ex.Message}");
            }

            Logger.Info($"[WriteService] All creation stages failed for {partGuid}. The object might not support this part type.");
            return null;
        }

        private Guid GetPartTypeGuid(string partName)
        {
            switch (partName?.ToLower())
            {
                case "source": return new Guid("528d1c06-a9c2-420d-bd35-21dca83f12ff");
                case "rules": return new Guid("9b0a32a3-de6d-4be1-a4dd-1b85d3741534");
                case "events": return new Guid("c414ed00-8cc4-4f44-8820-4baf93547173");
                case "webevents": return new Guid("c44bd5ff-f918-415b-98e6-aca44fed84fa");
                default: return Guid.Empty;
            }
        }

        private string GetGuid(object obj)
        {
            if (obj == null) return "";
            if (obj is Guid) return obj.ToString();
            var guidProp = obj.GetType().GetProperty("Guid");
            if (guidProp != null) return guidProp.GetValue(obj, null)?.ToString() ?? "";
            var idProp = obj.GetType().GetProperty("Id");
            if (idProp != null) return idProp.GetValue(obj, null)?.ToString() ?? "";
            var typeGuidProp = obj.GetType().GetProperty("TypeGuid");
            if (typeGuidProp != null) return typeGuidProp.GetValue(obj, null)?.ToString() ?? "";
            return "";
        }
    }
}
