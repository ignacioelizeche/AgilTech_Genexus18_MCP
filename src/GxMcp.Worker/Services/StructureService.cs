using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class StructureService
    {
        private readonly ObjectService _objectService;

        public StructureService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        public string UpdateVisualStructure(string targetName, string payload)
        {
            try
            {
                var obj = _objectService.FindObject(targetName);
                if (obj == null) return "{\"error\": \"Object not found\"}";
                var trn = obj as Transaction;
                if (trn == null) return "{\"error\": \"Object is not a Transaction\"}";

                var kb = trn.Model.KB;
                using (var sdkTrans = kb.BeginTransaction())
                {
                    try
                    {
                        var json = JObject.Parse(payload);
                        var children = json["children"] as JArray;
                        if (children == null) return "{\"error\": \"Invalid payload: missing children array\"}";

                        SyncVisualLevel(trn.Structure.Root, children);
                        
                        // Unified Save with detailed error reporting
                        trn.EnsureSave();
                        
                        sdkTrans.Commit();
                        
                        // Force a background flush and index update
                        _objectService.GetKbService().GetIndexCache().UpdateEntry(trn);
                        return "{\"status\": \"Success\"}";
                    }
                    catch (Exception ex)
                    {
                        sdkTrans.Rollback();
                        Logger.Error("UpdateVisualStructure Save Error: " + ex.ToString());
                        return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("UpdateVisualStructure Error: " + ex.ToString());
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private void SyncVisualLevel(TransactionLevel sdkLevel, JArray visualItems)
        {
            // 1. Identify Items to Delete
            var visualNames = new HashSet<string>(visualItems.Select(v => v["name"]?.ToString()), StringComparer.OrdinalIgnoreCase);
            
            // Delete Attributes
            var attrsToRemove = new List<dynamic>();
            if (sdkLevel.Attributes != null)
            {
                foreach (dynamic attr in sdkLevel.Attributes)
                {
                    if (!visualNames.Contains(attr.Name)) attrsToRemove.Add(attr);
                }
                foreach (dynamic dead in attrsToRemove) { try { sdkLevel.Attributes.Remove(dead); } catch { } }
            }

            // Delete SubLevels
            var levelsToRemove = new List<dynamic>();
            if (sdkLevel.Levels != null)
            {
                foreach (dynamic lvl in sdkLevel.Levels)
                {
                    if (!visualNames.Contains(lvl.Name)) levelsToRemove.Add(lvl);
                }
                foreach (dynamic dead in levelsToRemove) { try { sdkLevel.Levels.Remove(dead); } catch { } }
            }

            // 2. Add or Update Items
            foreach (var vItem in visualItems)
            {
                string name = vItem["name"]?.ToString();
                bool isLevel = (bool?)vItem["isLevel"] ?? false;
                if (string.IsNullOrEmpty(name)) continue;

                if (isLevel)
                {
                    // Find or Create Level
                    TransactionLevel targetLevel = null;
                    TransactionLevel parent = sdkLevel as TransactionLevel;
                    
                    if (sdkLevel.Levels != null)
                    {
                        foreach (dynamic lvl in sdkLevel.Levels) {
                            if (lvl.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { targetLevel = lvl; break; }
                        }
                    }

                    if (targetLevel == null && parent != null)
                    {
                        targetLevel = new TransactionLevel(parent.Structure);
                        targetLevel.Name = name;
                        parent.Levels.Add(targetLevel);
                    }

                    // Recursively sync children
                    var children = vItem["children"] as JArray;
                    if (children != null && targetLevel != null) SyncVisualLevel(targetLevel, children);
                }
                else
                {
                    // Find or Create Attribute
                    TransactionAttribute targetAttr = null;
                    TransactionLevel parent = sdkLevel as TransactionLevel;

                    if (sdkLevel.Attributes != null)
                    {
                        foreach (dynamic attr in sdkLevel.Attributes) {
                            if (attr.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { targetAttr = attr; break; }
                        }
                    }

                    if (targetAttr == null && parent != null)
                    {
                        var attrObj = _objectService.FindObject(name) as Artech.Genexus.Common.Objects.Attribute;
                        if (attrObj != null)
                        {
                            targetAttr = new TransactionAttribute(parent.Structure, attrObj);
                            targetAttr.Name = name;
                            parent.Attributes.Add(targetAttr);
                        }
                        else
                        {
                            // If attribute doesn't exist, we skip for now or we could create it
                            Logger.Debug("Attribute not found in KB, skipping creation for " + name);
                            continue; 
                        }
                    }

                    // Update Metadata
                    targetAttr.IsKey = (bool?)vItem["isKey"] ?? false;
                    
                    if (targetAttr.Attribute != null) 
                    {
                        bool isAttrGlobalModified = false;
                        string desc = vItem["description"]?.ToString();
                        string formula = vItem["formula"]?.ToString();
                        string nullable = vItem["nullable"]?.ToString();

                        if (desc != null && targetAttr.Attribute.Description != desc) {
                            targetAttr.Attribute.Description = desc;
                            isAttrGlobalModified = true;
                        }
                        
                        // Handle Formula Cleanup and Updates
                        if (formula != null) 
                        {
                            string currentFormula = targetAttr.Attribute.Formula != null ? targetAttr.Attribute.Formula.ToString() : "";
                            
                            if (string.IsNullOrWhiteSpace(formula)) {
                                // If UI says no formula, ensure the KB Attribute is absolutely clean of empty Formula objects
                                if (targetAttr.Attribute.Formula != null) {
                                    targetAttr.Attribute.Formula = null;
                                    isAttrGlobalModified = true;
                                }
                            } else if (formula != currentFormula) {
                                // Only parse and set if it's different
                                try {
                                    targetAttr.Attribute.Formula = Artech.Genexus.Common.Objects.Formula.Parse(formula, targetAttr.Attribute, null);
                                    targetAttr.IsKey = false; // SDK Error: Formulas cannot be primary keys
                                    isAttrGlobalModified = true;
                                } catch {
                                    // Ignore if parsing fails
                                }
                            }
                        }
                        
                        // Fallback check: if there is an existing formula, it cannot be a key
                        if (targetAttr.Attribute.Formula != null && !string.IsNullOrWhiteSpace(targetAttr.Attribute.Formula.ToString()))
                        {
                            targetAttr.IsKey = false;
                        }
                        
                        if (nullable != null)
                        {
                            // Map UI string to eNullable enum values (0=No, 1=Yes, 2=Managed)
                            int gxNullableValue = 0; // Default to No (False)
                            if (nullable.Equals("Yes", StringComparison.OrdinalIgnoreCase) || 
                                nullable.Equals("Nullable", StringComparison.OrdinalIgnoreCase) ||
                                nullable.Equals("1") || nullable.Equals("True", StringComparison.OrdinalIgnoreCase)) gxNullableValue = 1;
                            else if (nullable.Equals("Managed", StringComparison.OrdinalIgnoreCase) || 
                                     nullable.Equals("Compatible", StringComparison.OrdinalIgnoreCase) ||
                                     nullable.Equals("2")) gxNullableValue = 2;

                            try {
                                // TransactionAttribute.IsNullable is the most direct way to read/write this.
                                // We use dynamic to handle the internal SDK Enum type if needed, or just cast to int.
                                if ((int)targetAttr.IsNullable != gxNullableValue) {
                                    targetAttr.IsNullable = (dynamic)gxNullableValue;
                                    // Nullable also fundamentally changes the Attribute definition if it's the global one.
                                    // Ensure Attribute object also gets updated if it has a direct property (sometimes synced automatically by SDK)
                                    try {
                                        targetAttr.Attribute.SetPropertyValue("Nullable", gxNullableValue);
                                    } catch { }
                                    isAttrGlobalModified = true;
                                }
                            } catch (Exception ex) {
                                Logger.Debug($"Nullable update failed for {targetAttr.Name}: {ex.Message}");
                            }
                        }

                        string userType = vItem["type"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(userType)) 
                        {
                            try {
                                var currentTypeStr = targetAttr.Attribute.Type.ToString();
                                if (currentTypeStr != userType) {
                                    System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(userType, @"^([a-zA-Z]+)(?:\((\d+)(?:[,.](\d+))?\))?");
                                    if (match.Success) {
                                        string tName = match.Groups[1].Value.ToUpper();
                                        int len = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
                                        int dec = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
                                        
                                        if (Enum.TryParse<Artech.Genexus.Common.eDBType>(tName, true, out var eType)) {
                                            targetAttr.Attribute.Type = eType;
                                            if (len > 0) targetAttr.Attribute.Length = len;
                                            if (dec > 0) targetAttr.Attribute.Decimals = dec;
                                            isAttrGlobalModified = true;
                                        }
                                    }
                                }
                            } catch (Exception ex) {
                                Logger.Debug("Type parse failed: " + ex.Message);
                            }
                        }

                        // If we altered the global Attribute, we MUST save it.
                        // To avoid large transaction locks and timeouts, we only save if absolutely necessary.
                        // However, many Attributes changed at once might still cause pressure.
                        if (isAttrGlobalModified) {
                            targetAttr.Attribute.Save(); // Use Save() instead of EnsureSave() for potentially fewer side effects in loop
                        }
                    }
                }
            }
        }

        public string GetVisualStructure(string targetName)
        {
            try
            {
                var obj = _objectService.FindObject(targetName);
                if (obj == null) return "{\"error\": \"Object not found\"}";
                
                var result = new JObject();
                result["name"] = obj.Name;
                result["type"] = obj.TypeDescriptor.Name;
                result["description"] = obj.Description;

                if (obj is Transaction trn)
                {
                    result["children"] = SerializeVisualLevel(trn.Structure.Root);
                }
                else if (obj is Table tbl)
                {
                    result["children"] = SerializeTableStructure(tbl);
                }
                else
                {
                    return "{\"error\": \"Object is not a Transaction or Table\"}";
                }
                
                return result.ToString();
            }
            catch (Exception ex)
            {
                Logger.Error("GetVisualStructure Error: " + ex.ToString());
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private JArray SerializeTableStructure(Table tbl)
        {
            var children = new JArray();
            try
            {
                dynamic dStructure = ((dynamic)tbl).TableStructure;
                foreach (dynamic attr in dStructure.Attributes)
                {
                    var item = new JObject();
                    item["name"] = attr.Name;
                    item["isKey"] = (bool)attr.IsKey;
                    item["isLevel"] = false;
                    
                    string typeStr = "Unknown";
                    string desc = "";
                    string formula = "";

                    try {
                        if (attr.Attribute != null) {
                            if (attr.Attribute.Type != null) typeStr = attr.Attribute.Type.ToString();
                            
                            // Include Length and Decimals for relevant types
                            int len = attr.Attribute.Length;
                            int dec = attr.Attribute.Decimals;
                            if (len > 0)
                            {
                                if (dec > 0) typeStr += $"({len},{dec})";
                                else typeStr += $"({len})";
                            }

                            desc = attr.Attribute.Description?.ToString() ?? "";
                            formula = attr.Attribute.Formula?.ToString() ?? "";
                        }
                    } catch { }

                    item["type"] = typeStr;
                    item["description"] = desc;
                    item["formula"] = formula;
                    
                    try {
                        int nVal = (int)attr.IsNullable;
                        item["nullable"] = (nVal == 1) ? "Yes" : (nVal == 2 ? "Managed" : "No");
                    } catch {
                        item["nullable"] = "No";
                    }
                    
                    children.Add(item);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("SerializeTableStructure Error: " + ex.Message);
            }
            return children;
        }

        private JArray SerializeVisualLevel(TransactionLevel level)
        {
            var children = new JArray();

            // Attributes
            if (level.Attributes != null)
            {
                foreach (dynamic attr in level.Attributes)
                {
                    var item = new JObject();
                    item["name"] = attr.Name;
                    item["isKey"] = attr.IsKey;
                    item["isLevel"] = false;

                    string typeStr = "Unknown";
                    string desc = "";
                    string formula = "";
                    string isNullable = "No";

                    try {
                        // Robust type extraction (matches StructureParser.cs pattern)
                        try {
                            if (attr.Type != null) typeStr = attr.Type.ToString();
                        } catch {
                            try {
                                if (attr.Attribute != null && attr.Attribute.Type != null) {
                                    typeStr = attr.Attribute.Type.ToString();
                                }
                            } catch { }
                        }

                        if (attr.Attribute != null) {
                            desc = (attr.Attribute.Description != null) ? attr.Attribute.Description.ToString() : "";
                            formula = (attr.Attribute.Formula != null) ? attr.Attribute.Formula.ToString() : "";
                            
                            // Include Length and Decimals for relevant types
                            int len = attr.Attribute.Length;
                            int dec = attr.Attribute.Decimals;
                            if (len > 0)
                            {
                                if (dec > 0) typeStr += $"({len},{dec})";
                                else typeStr += $"({len})";
                            }

                            // Use the native IsNullable property discovered via reflection
                            try {
                                int nVal = (int)attr.IsNullable;
                                if (nVal == 1) isNullable = "Yes";
                                else if (nVal == 2) isNullable = "Managed";
                                else isNullable = "No";
                            } catch {
                                isNullable = "No";
                            }
                        }
                    } catch { }

                    item["type"] = typeStr;
                    item["description"] = desc;
                    item["formula"] = formula;
                    item["nullable"] = isNullable;
                    children.Add(item);
                }
            }

            // SubLevels
            if (level.Levels != null)
            {
                foreach (dynamic subLevel in level.Levels)
                {
                    var levelItem = new JObject();
                    levelItem["name"] = subLevel.Name;
                    levelItem["isLevel"] = true;
                    levelItem["children"] = SerializeVisualLevel(subLevel);
                    children.Add(levelItem);
                }
            }

            return children;
        }

        public string GetVisualIndexes(string targetName)
        {
            try
            {
                var obj = _objectService.FindObject(targetName);
                if (obj == null) return "{\"error\": \"Object not found\"}";

                dynamic tbl = null;
                if (obj is Table t) tbl = t;
                else if (obj is Transaction trn) tbl = trn.Structure.Root.AssociatedTable;

                if (tbl == null) return "{\"error\": \"Object has no associated table\"}";

                var result = new JObject();
                result["name"] = tbl.Name;
                var indexes = new JArray();

                dynamic dIndexesPart = tbl.TableIndexes;
                if (dIndexesPart != null && dIndexesPart.Indexes != null)
                {
                    foreach (dynamic idxObj in dIndexesPart.Indexes)
                    {
                        dynamic idx = idxObj.Index; // Access the real Index object
                        if (idx == null) continue;

                        var indexItem = new JObject();
                        indexItem["name"] = idx.Name;
                        
                        // Discover primary/unique status
                        bool isPrimary = false;
                        bool isUnique = false;
                        try {
                            string typeStr = idx.IndexType?.ToString() ?? "";
                            isPrimary = typeStr.Contains("Primary");
                            isUnique = typeStr.Contains("Unique") || isPrimary;
                        } catch { }

                        indexItem["isPrimary"] = isPrimary;
                        indexItem["isUnique"] = isUnique;
                        
                        var attrs = new JArray();
                        try {
                            dynamic members = idx.IndexStructure.Members;
                            if (members != null)
                            {
                                foreach (dynamic member in members)
                                {
                                    var attrObj = new JObject();
                                    attrObj["name"] = member.Attribute?.Name ?? member.Name;
                                    try {
                                        attrObj["isAscending"] = member.Order.ToString().Contains("Ascending");
                                    } catch {
                                        attrObj["isAscending"] = true;
                                    }
                                    attrs.Add(attrObj);
                                }
                            }
                        } catch (Exception ex) {
                            Logger.Error("Error retrieving index attributes: " + ex.Message);
                        }
                        indexItem["attributes"] = attrs;
                        indexes.Add(indexItem);
                    }
                }

                result["indexes"] = indexes;
                return result.ToString();
            }
            catch (Exception ex)
            {
                Logger.Error("GetVisualIndexes Error: " + ex.Message);
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }
    }
}
