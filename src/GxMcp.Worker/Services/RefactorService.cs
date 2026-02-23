using System;
using System.Linq;
using System.Collections.Generic;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using Artech.Genexus.Common.Parts;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class RefactorService
    {
        private readonly KbService _kbService;
        private readonly ObjectService _objectService;
        private readonly IndexCacheService _indexCacheService;

        public RefactorService(KbService kbService, ObjectService objectService, IndexCacheService indexCacheService)
        {
            _kbService = kbService;
            _objectService = objectService;
            _indexCacheService = indexCacheService;
        }

        public string Refactor(string target, string action, string payload)
        {
            try {
                var data = JObject.Parse(payload);
                string oldName = data["oldName"]?.ToString();
                string newName = data["newName"]?.ToString();

                if (action == "RenameVariable" || (oldName != null && oldName.StartsWith("&"))) {
                    return RenameVariable(target, oldName, newName);
                }
                
                if (action == "RenameAttribute" || (oldName != null && !oldName.StartsWith("&"))) {
                    return RenameAttribute(oldName, newName);
                }

                return "{\"error\":\"Refactor action not found\"}";
            } catch (Exception ex) {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string RenameAttribute(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName))
                return "{\"error\":\"Old and new names are required\"}";

            var kb = _kbService.GetKB();
            if (kb == null) return "{\"error\":\"KB not open\"}";

            // 1. Find the Attribute definition
            var attrObj = _objectService.FindObject(oldName);
            if (attrObj == null || !attrObj.TypeDescriptor.Name.Equals("Attribute", StringComparison.OrdinalIgnoreCase))
                return "{\"error\":\"Attribute '" + oldName + "' not found\"}";

            Logger.Info($"Renaming Attribute {oldName} to {newName} Globally");

            // 2. Perform SDK Rename (This triggers internal GeneXus reorg tracking)
            attrObj.Name = newName;
            attrObj.Save();

            // 3. Find Affected Objects using Index
            var index = _indexCacheService.GetIndex();
            var affectedObjects = new List<string>();
            if (index != null && index.Objects.TryGetValue(oldName, out var entry))
            {
                if (entry.CalledBy != null) affectedObjects.AddRange(entry.CalledBy);
            }

            // 4. Update Source Code in all affected objects
            string pattern = @"(?i)\b" + System.Text.RegularExpressions.Regex.Escape(oldName) + @"\b";
            string replacement = newName;
            int updatedCount = 0;

            foreach (var objName in affectedObjects.Distinct())
            {
                var obj = _objectService.FindObject(objName);
                if (obj == null) continue;

                bool changed = false;
                foreach (var part in obj.Parts.Cast<KBObjectPart>())
                {
                    if (part is ISource sourcePart)
                    {
                        string original = sourcePart.Source;
                        if (!string.IsNullOrEmpty(original))
                        {
                            string updated = System.Text.RegularExpressions.Regex.Replace(original, pattern, replacement);
                            if (updated != original)
                            {
                                sourcePart.Source = updated;
                                changed = true;
                            }
                        }
                    }
                }

                if (changed)
                {
                    obj.Save();
                    _indexCacheService.UpdateEntry(obj);
                    updatedCount++;
                }
            }

            // Update the index for the attribute itself
            _indexCacheService.UpdateEntry(attrObj);

            return "{\"status\":\"Success\", \"affectedObjects\":" + updatedCount + ", \"message\":\"Attribute renamed. A Reorganization may be required.\"}";
        }

        private string RenameVariable(string target, string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName))
                return "{\"error\":\"Old and new names are required\"}";

            // Normalize names (remove & if present)
            string cleanOld = oldName.StartsWith("&") ? oldName.Substring(1) : oldName;
            string cleanNew = newName.StartsWith("&") ? newName.Substring(1) : newName;

            var obj = _objectService.FindObject(target);
            if (obj == null) return "{\"error\":\"Object not found\"}";

            Logger.Info($"Renaming variable &{cleanOld} to &{cleanNew} in {obj.Name}");

            bool changed = false;

            // 1. Update Variables Part
            var varPart = obj.Parts.Get<VariablesPart>();
            if (varPart != null) {
                var v = varPart.Variables.FirstOrDefault(x => x.Name.Equals(cleanOld, StringComparison.OrdinalIgnoreCase));
                if (v != null) {
                    v.Name = cleanNew;
                    changed = true;
                    Logger.Info("Updated variable definition.");
                }
            }

            // 2. Update Source Parts (Rules, Events, Source, etc.)
            // We use a regex for safe replacement (matching &VarName but not &VarNameExtra)
            string pattern = @"(?i)&" + System.Text.RegularExpressions.Regex.Escape(cleanOld) + @"\b";
            string replacement = "&" + cleanNew;

            foreach (var part in obj.Parts.Cast<KBObjectPart>()) {
                if (part is ISource sourcePart) {
                    string original = sourcePart.Source;
                    if (!string.IsNullOrEmpty(original)) {
                        string updated = System.Text.RegularExpressions.Regex.Replace(original, pattern, replacement);
                        if (updated != original) {
                            sourcePart.Source = updated;
                            changed = true;
                            Logger.Info($"Updated part: {part.TypeDescriptor.Name}");
                        }
                    }
                }
            }

            if (changed) {
                obj.Save();
                _objectService.GetKbService().GetIndexCache().UpdateEntry(obj);
                return "{\"status\":\"Success\"}";
            }

            return "{\"status\":\"No changes made\"}";
        }
    }
}
