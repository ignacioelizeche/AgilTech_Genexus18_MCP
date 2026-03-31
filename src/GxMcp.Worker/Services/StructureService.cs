using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Artech.Genexus.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services.Structure;

namespace GxMcp.Worker.Services
{
    public class StructureService
    {
        private readonly ObjectService _objectService;
        private readonly VisualStructureService _visualStructureService;
        private readonly IndexService _indexService;
        private readonly SDTService _sdtService;

        public StructureService(ObjectService objectService)
        {
            _objectService = objectService;
            _visualStructureService = new VisualStructureService(objectService);
            _indexService = new IndexService(objectService);
            _sdtService = new SDTService(objectService);
        }

        public string UpdateVisualStructure(string targetName, string payload)
        {
            try {
                var obj = _objectService.FindObject(targetName);
                if (obj == null) return HealingService.FormatNotFoundError(targetName, _objectService.GetKbService().GetIndexCache().GetIndex());
                var trn = obj as Transaction;
                if (trn == null) return Models.McpResponse.Error("Object is not a Transaction", targetName, "Structure", "Visual structure updates currently support Transaction objects only.", obj.Name, obj.TypeDescriptor?.Name);

                using (var sdkTrans = trn.Model.KB.BeginTransaction()) {
                    try {
                        var json = JObject.Parse(payload);
                        var children = json["children"] as JArray;
                        if (children == null) return Models.McpResponse.Error("Invalid payload", targetName, "Structure", "The payload must contain a 'children' array for visual structure updates.");
                        
                        // Chamada otimizada com Batch Save interno
                        _visualStructureService.SyncVisualStructure(trn, children);
                        
                        trn.EnsureSave();
                        sdkTrans.Commit();
                        
                        _objectService.GetKbService().GetIndexCache().UpdateEntry(trn);
                        return "{\"status\": \"Success\"}";
                    } catch (Exception ex) {
                        sdkTrans.Rollback();
                        return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
                    }
                }
            } catch (Exception ex) { return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}"; }
        }

        public string GetVisualStructure(string targetName)
        {
            try {
                Logger.Info($"[StructureService] Loading visual structure for: {targetName}");
                var obj = _objectService.FindObject(targetName);
                if (obj == null) return HealingService.FormatNotFoundError(targetName, _objectService.GetKbService().GetIndexCache().GetIndex());
                
                Logger.Info($"[StructureService] Found object: {obj.Name} ({obj.TypeDescriptor.Name})");

                if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                {
                    return _sdtService.GetSDTStructure(targetName);
                }

                var result = new JObject { ["name"] = obj.Name, ["type"] = obj.TypeDescriptor.Name, ["description"] = obj.Description };
                if (obj is Transaction trn) {
                    Logger.Info($"[StructureService] Serializing Transaction Level: {trn.Name}");
                    result["children"] = _visualStructureService.SerializeVisualLevel(trn.Structure.Root);
                }
                else if (obj is Table tbl) {
                    Logger.Info($"[StructureService] Serializing Table Structure: {tbl.Name}");
                    result["children"] = SerializeTableStructure(tbl);
                }
                else {
                    Logger.Error($"[StructureService] Invalid object type for visual structure: {obj.TypeDescriptor.Name}");
                    return Models.McpResponse.Error("Invalid object type", targetName, "Structure", "Visual structure is available only for Transaction, Table or SDT objects.", obj.Name, obj.TypeDescriptor?.Name);
                }
                
                Logger.Info($"[StructureService] Successfully serialized structure for {obj.Name}");
                return result.ToString();
            } catch (Exception ex) { 
                Logger.Error($"[StructureService] Error loading visual structure: {ex.Message}\n{ex.StackTrace}");
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}"; 
            }
        }

        public string GetVisualIndexes(string targetName) => _indexService.GetVisualIndexes(targetName);
        
        public string GetLogicStructure(string targetName)
        {
            try {
                var obj = _objectService.FindObject(targetName);
                if (obj == null) return HealingService.FormatNotFoundError(targetName, _objectService.GetKbService().GetIndexCache().GetIndex());

                var result = new JObject { ["name"] = obj.Name, ["type"] = obj.TypeDescriptor.Name };
                var subs = new JArray();
                var events = new JArray();

                // 1. Get Source Part (Subs usually here)
                try {
                    var sourcePart = obj.Parts.Get<global::Artech.Genexus.Common.Parts.SourcePart>();
                    if (sourcePart != null) ExtractLogicItems(sourcePart.Source, subs, events);
                } catch { }

                // 2. Get Events Part (Events usually here)
                try {
                    var eventsPart = obj.Parts.Get<global::Artech.Genexus.Common.Parts.EventsPart>();
                    if (eventsPart != null) ExtractLogicItems(eventsPart.Source, subs, events);
                } catch { }

                result["subs"] = subs;
                result["events"] = events;
                return result.ToString();
            }
            catch (Exception ex) { return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}"; }
        }

        private void ExtractLogicItems(string source, JArray subs, JArray events)
        {
            if (string.IsNullOrEmpty(source)) return;

            // Sub Extraction
            var subMatches = System.Text.RegularExpressions.Regex.Matches(source, @"(?i)\bsub\s+['""]?([\w\.]+)['""]?");
            foreach (System.Text.RegularExpressions.Match match in subMatches)
            {
                string name = match.Groups[1].Value;
                if (!subs.Any(s => s.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)))
                    subs.Add(name);
            }

            // Event Extraction
            var eventMatches = System.Text.RegularExpressions.Regex.Matches(source, @"(?i)\bevent\s+['""]?([\w\.]+)['""]?");
            foreach (System.Text.RegularExpressions.Match match in eventMatches)
            {
                string name = match.Groups[1].Value;
                if (!events.Any(e => e.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)))
                    events.Add(name);
            }
        }

        private JArray SerializeTableStructure(Table tbl)
        {
            var children = new JArray();
            dynamic dStructure = ((dynamic)tbl).TableStructure;
            if (dStructure != null && dStructure.Attributes != null) {
                foreach (dynamic attr in dStructure.Attributes) children.Add(VisualStructureMapper.MapAttribute(attr));
            }
            return children;
        }
    }
}
