using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using System.Reflection;

namespace GxMcp.Worker.Services
{
    public class WriteService
    {
        private readonly ObjectService _objectService;
        private static readonly object _flushLock = new object();
        private static System.Timers.Timer _flushTimer;
        private static bool _pendingCommit = false;

        public WriteService(ObjectService objectService)
        {
            _objectService = objectService;
            InitializeFlushTimer();
        }

        private void InitializeFlushTimer()
        {
            if (_flushTimer != null) return;
            lock (_flushLock)
            {
                if (_flushTimer != null) return;
                _flushTimer = new System.Timers.Timer(2000); // 2 seconds debounce
                _flushTimer.AutoReset = false;
                _flushTimer.Elapsed += (s, e) => FlushBackground();
            }
        }

        private void FlushBackground()
        {
            if (!_pendingCommit) return;
            
            lock (_flushLock)
            {
                if (!_pendingCommit) return;
                try
                {
                    Logger.Info("[BACKGROUND-FLUSH] Starting commits...");
                    var kb = _objectService.GetKbService().GetKB();
                    if (kb == null) return;

                    // Commits
                    var model = kb.DesignModel;
                    if (model != null) {
                        try {
                            var modelCommit = model.GetType().GetMethod("Commit", BindingFlags.Public | BindingFlags.Instance);
                            modelCommit?.Invoke(model, null);
                            Logger.Info("[BACKGROUND-FLUSH] Model.Commit() successful.");
                        } catch (Exception ex) { Logger.Debug("[BACKGROUND-FLUSH] Model.Commit skipped: " + ex.Message); }
                    }
                    
                    try {
                        var kbCommit = kb.GetType().GetMethod("Commit", BindingFlags.Public | BindingFlags.Instance);
                        kbCommit?.Invoke(kb, null);
                        Logger.Info("[BACKGROUND-FLUSH] KB.Commit() successful.");
                    } catch (Exception ex) { Logger.Debug("[BACKGROUND-FLUSH] KB.Commit skipped: " + ex.Message); }

                    _pendingCommit = false;
                    Logger.Info("[BACKGROUND-FLUSH] Full commit cycle complete.");
                }
                catch (Exception ex)
                {
                    Logger.Error("[BACKGROUND-FLUSH] ERROR: " + ex.Message);
                }
            }
        }

        private void ScheduleFlush()
        {
            _pendingCommit = true;
            _flushTimer.Stop();
            _flushTimer.Start();
        }

        public string WriteObject(string target, string partName, string code)
        {
            try
            {
                Logger.Info(string.Format("[DEBUG-SAVE] Request received for {0} (Part: {1}, Code Length: {2})", target, partName, code?.Length ?? 0));
                
                var obj = _objectService.FindObject(target);
                if (obj == null) {
                    Logger.Error("[DEBUG-SAVE] Object NOT FOUND: " + target);
                    return "{\"error\": \"Object not found\"}";
                }

                Logger.Debug(string.Format("[DEBUG-SAVE] Object Found: {0} ({1})", obj.Name, obj.TypeDescriptor.Name));

                // Log all parts for debugging
                foreach (var p in obj.Parts) {
                    Logger.Debug(string.Format("[DEBUG-SAVE] Available Part: {0} (GUID: {1}, Type: {2})", p.TypeDescriptor?.Name, p.Type, p.GetType().Name));
                }

                Guid partGuid = MapLogicalPartToGuid(obj.TypeDescriptor.Name, partName);
                global::Artech.Architecture.Common.Objects.KBObjectPart part = null;
                
                // Strategy 1: Map by logical GUID
                if (partGuid != Guid.Empty) {
                    part = obj.Parts.Cast<global::Artech.Architecture.Common.Objects.KBObjectPart>().FirstOrDefault(p => p.Type == partGuid);
                }

                // Strategy 2: Fallback to name-based or interface-based matching
                if (part == null)
                {
                    string pName = partName.ToLower();
                    foreach (global::Artech.Architecture.Common.Objects.KBObjectPart p in obj.Parts)
                    {
                        if (pName == "variables" && p is global::Artech.Genexus.Common.Parts.VariablesPart) { part = p; break; }
                        if ((pName == "source" || pName == "code" || pName == "events") && p is global::Artech.Architecture.Common.Objects.ISource) { part = p; break; }
                        if (pName == "structure" && (p.GetType().Name.Contains("Structure") || p.TypeDescriptor.Name.Equals("Structure", StringComparison.OrdinalIgnoreCase))) { part = p; break; }
                    }
                }

                if (part == null) {
                    Logger.Error("[DEBUG-SAVE] Part NOT FOUND in object: " + partName);
                    return "{\"error\": \"Part not found in " + obj.TypeDescriptor.Name + "\"}";
                }

                Logger.Info(string.Format("[DEBUG-SAVE] Target Part Selected: {0} (GUID: {1}, Type: {2})", part.TypeDescriptor?.Name, part.Type, part.GetType().Name));

                // 1. SET CONTENT
                bool contentSet = false;
                if (part is global::Artech.Genexus.Common.Parts.VariablesPart varPart)
                {
                    Logger.Debug("[DEBUG-SAVE] Updating Variables via SetVariablesFromText...");
                    VariableInjector.SetVariablesFromText(varPart, code);
                    contentSet = true;
                }
                else if (part is global::Artech.Architecture.Common.Objects.ISource sourcePart)
                {
                    Logger.Debug("[DEBUG-SAVE] Updating ISource.Source content...");
                    sourcePart.Source = code;
                    contentSet = true;
                }
                else
                {
                    // Fallback for non-ISource parts (like Structure)
                    Logger.Debug("[DEBUG-SAVE] Attempting generic persistence for non-ISource part...");
                    try {
                        // For structured parts, we try SerializeFromXml first if it looks like XML
                        if (code.Trim().StartsWith("<")) {
                            part.DeserializeFromXml(code);
                            contentSet = true;
                            Logger.Debug("[DEBUG-SAVE] Content set via DeserializeFromXml.");
                        } else {
                            // Try to find a property like 'Source' or 'Content' via reflection
                            var contentProp = part.GetType().GetProperty("Source", BindingFlags.Public | BindingFlags.Instance)
                                           ?? part.GetType().GetProperty("Content", BindingFlags.Public | BindingFlags.Instance);
                            if (contentProp != null && contentProp.CanWrite) {
                                contentProp.SetValue(part, code);
                                contentSet = true;
                                Logger.Debug("[DEBUG-SAVE] Content set via property: " + contentProp.Name);
                            }
                        }
                    } catch (Exception ex) { Logger.Error("[DEBUG-SAVE] Generic persistence FAILED: " + ex.Message); }
                }

                if (!contentSet) {
                    Logger.Warn("[DEBUG-SAVE] No suitable method found to update part content.");
                }

                // 2. FORCE DIRTY (Crucial)
                try {
                    // Mark Part as Dirty
                    var pType = part.GetType();
                    var pDirtyProp = pType.GetProperty("Dirty", BindingFlags.Public | BindingFlags.Instance) 
                                  ?? pType.GetProperty("IsDirty", BindingFlags.Public | BindingFlags.Instance);
                    if (pDirtyProp != null) {
                        pDirtyProp.SetValue(part, true);
                        Logger.Debug("[DEBUG-SAVE] Part property '" + pDirtyProp.Name + "' set to TRUE");
                    }

                    // Mark Header Object as Dirty (Essential for Save)
                    var oType = obj.GetType();
                    var oDirtyProp = oType.GetProperty("Dirty", BindingFlags.Public | BindingFlags.Instance)
                                  ?? oType.GetProperty("IsDirty", BindingFlags.Public | BindingFlags.Instance);
                    if (oDirtyProp != null) {
                        oDirtyProp.SetValue(obj, true);
                        Logger.Debug("[DEBUG-SAVE] Object property '" + oDirtyProp.Name + "' set to TRUE");
                    }
                } catch (Exception ex) { Logger.Debug("[DEBUG-SAVE] Force Dirty failed: " + ex.Message); }

                // 3. PERSISTENCE SEQUENCE
                try
                {
                    var kb = _objectService.GetKbService().GetKB();
                    if (kb == null) throw new Exception("KB not opened");

                    // 1. Start Transaction
                    Logger.Info("[DEBUG-SAVE] Starting SDK Transaction...");
                    var transaction = kb.BeginTransaction();

                    try {
                        // 2. Checkout
                        try {
                            var checkoutMethod = obj.GetType().GetMethod("Checkout", BindingFlags.Public | BindingFlags.Instance);
                            checkoutMethod?.Invoke(obj, null);
                            Logger.Debug("[DEBUG-SAVE] SDK Checkout invoked.");
                        } catch { }

                        // 3. Save Part (CRITICAL: Save the part explicitly first)
                        Logger.Info(string.Format("[DEBUG-SAVE] Invoking part.Save() for {0}...", part.TypeDescriptor?.Name));
                        part.Save();
                        Logger.Info("[DEBUG-SAVE] part.Save() completed.");

                        // 4. Save Object
                        Logger.Info("[DEBUG-SAVE] Invoking obj.Save()...");
                        obj.Save();
                        Logger.Info("[DEBUG-SAVE] obj.Save() completed.");

                        // 5. Transaction Commit
                        Logger.Info("[DEBUG-SAVE] Committing SDK Transaction...");
                        transaction.Commit();
                        Logger.Info("[DEBUG-SAVE] SDK Transaction Committed.");
                    }
                    catch (Exception ex) {
                        Logger.Error("[DEBUG-SAVE] Transaction Error: " + ex.Message);
                        transaction.Rollback();
                        throw;
                    }

                    _objectService.GetKbService().GetIndexCache().UpdateEntry(obj);
                    
                    // Final persistence in background for "Fast Save"
                    ScheduleFlush();

                    Logger.Info("[DEBUG-SAVE] SAVE & COMMIT COMPLETE.");
                    return "{\"status\": \"Success\"}";
                }
                catch (Exception saveEx)
                {
                    Logger.Error("[DEBUG-SAVE] CRITICAL SDK EXCEPTION: " + saveEx.ToString());
                    return "{\"error\": \"SDK Save failed: " + saveEx.Message + "\"}";
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[DEBUG-SAVE] OUTER EXCEPTION: " + ex.ToString());
                return "{\"error\": \"" + ex.Message + "\"}";
            }
        }

        public string AddVariable(string target, string varName, string typeName = null)
        {
            try
            {
                var obj = _objectService.FindObject(target);
                if (obj == null) return "{\"error\": \"Object not found\"}";

                var varPart = obj.Parts.Get<global::Artech.Genexus.Common.Parts.VariablesPart>();
                if (varPart == null) return "{\"error\": \"Variables part not found\"}";

                if (varPart.Variables.Any(v => string.Equals(v.Name, varName, StringComparison.OrdinalIgnoreCase)))
                    return "{\"status\": \"Variable already exists\"}";

                global::Artech.Genexus.Common.Variable newVar = new global::Artech.Genexus.Common.Variable(varPart);
                newVar.Name = varName;
                
                if (!string.IsNullOrEmpty(typeName))
                {
                    if (Enum.TryParse<global::Artech.Genexus.Common.eDBType>(typeName, true, out var dbType))
                    {
                        newVar.Type = dbType;
                    }
                }
                else
                {
                    newVar.Type = global::Artech.Genexus.Common.eDBType.VARCHAR;
                    newVar.Length = 100;
                }

                varPart.Variables.Add(newVar);
                obj.Save();
                ScheduleFlush();
                
                return "{\"status\": \"Success\"}";
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private Guid MapLogicalPartToGuid(string objType, string logicalPart)
        {
            string p = logicalPart.ToLower();
            
            // GUIDs Oficiais GeneXus 18
            if (objType.Equals("Procedure", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "source" || p == "code") return Guid.Parse("c5f0ef88-9ef8-4218-bf76-915024b3c48f");
                if (p == "rules") return Guid.Parse("9b0a32a3-de6d-4be1-a4dd-1b85d3741534");
                if (p == "variables") return Guid.Parse("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
                if (p == "help") return Guid.Parse("017ea008-6202-4468-a400-3f412c938473");
            }
            
            if (objType.Equals("WebPanel", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "events" || p == "source" || p == "code") return Guid.Parse("c44bd5ff-f918-415b-98e6-aca44fed84fa");
                if (p == "rules") return Guid.Parse("9b0a32a3-de6d-4be1-a4dd-1b85d3741534");
                if (p == "variables") return Guid.Parse("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
                if (p == "layout") return Guid.Parse("ad3ca970-19d0-44e1-a7b7-db05556e820c");
                if (p == "webform") return Guid.Parse("d24a58ad-57ba-41b7-9e6e-eaca3543c778");
            }

            if (objType.Equals("Transaction", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "structure") return Guid.Parse("1608677c-a7a2-4a00-8809-6d2466085a5a");
                if (p == "events" || p == "source" || p == "code") return Guid.Parse("c44bd5ff-f918-415b-98e6-aca44fed84fa");
                if (p == "rules") return Guid.Parse("9b0a32a3-de6d-4be1-a4dd-1b85d3741534");
                if (p == "variables") return Guid.Parse("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
                if (p == "webform") return Guid.Parse("d24a58ad-57ba-41b7-9e6e-eaca3543c778");
            }

            if (objType.Equals("DataProvider", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "source" || p == "code") return Guid.Parse("91705646-6086-4f32-8871-08149817e754");
                if (p == "variables") return Guid.Parse("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
                if (p == "help") return Guid.Parse("017ea008-6202-4468-a400-3f412c938473");
            }

            if (objType.Equals("SDT", StringComparison.OrdinalIgnoreCase) || objType.Equals("StructuredDataType", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "structure" || p == "source") return Guid.Parse("8597371d-1941-4c12-9c17-48df9911e2f3");
            }

            return ObjectService.GetPartGuid(p);
        }
    }
}
