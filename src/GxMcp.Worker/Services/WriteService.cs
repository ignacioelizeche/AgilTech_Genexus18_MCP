using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class WriteService
    {
        private readonly ObjectService _objectService;

        // GUIDs
        private static readonly Guid PART_PROCEDURE = new Guid("528d1c06-a9c2-420d-bd35-21dca83f12ff");
        private static readonly Guid PART_RULES     = new Guid("9b0a32a3-de6d-4be1-a4dd-1b85d3741534");
        private static readonly Guid PART_EVENTS    = new Guid("c44bd5ff-f918-415b-98e6-aca44fed84fa");
        private static readonly Guid PART_VARIABLES = new Guid("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
        private static readonly Guid PART_WEB_FORM  = new Guid("d24a58ad-57ba-41b7-9e6e-eaca3543c778");

        public WriteService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        public string WriteObject(string target, string partName, string code)
        {
            try
            {
                var obj = _objectService.FindObject(target);
                if (obj == null) return "{\"error\": \"Object not found\"}";

                Guid partGuid = MapLogicalPartToGuid(obj.TypeDescriptor.Name, partName);
                
                global::Artech.Architecture.Common.Objects.KBObjectPart part = null;
                foreach (global::Artech.Architecture.Common.Objects.KBObjectPart p in obj.Parts)
                {
                    if (p.Type == partGuid) { part = p; break; }
                }

                if (part == null) return "{\"error\": \"Part '" + partName + "' not found in this object.\"}";

                var sourcePart = part as global::Artech.Architecture.Common.Objects.ISource;
                if (sourcePart == null) return "{\"error\": \"Part is not a source part\"}";

                sourcePart.Source = code;

                // Phase 1: Surgical Variable Injection
                if (partGuid == PART_PROCEDURE || partGuid == PART_EVENTS)
                {
                    VariableInjector.InjectVariables(obj, code);
                    
                    // Phase 16: Table Dependency Injection (Frente 16)
                    var index = _objectService.GetIndex();
                    TableDependencyInjector.InjectTableDependencies(obj, code, index);
                }

                try
                {
                    obj.Save();
                }
                catch (Exception saveEx)
                {
                    Logger.Error($"Native SDK Save Exception on {obj.Name}: {saveEx.Message}");
                }

                // Phase 2: High-Fidelity Diagnostics (Messages via SaveOutput)
                var output = obj.SaveOutput;
                var msgs = new JArray();
                bool hasErrors = false;

                if (output != null)
                {
                    hasErrors = output.HasErrors;
                    try
                    {
                        foreach (var m in output)
                        {
                            var jm = new JObject();
                            if (m is global::Artech.Common.Diagnostics.OutputError err)
                            {
                                jm["id"] = err.ErrorCode;
                                jm["text"] = err.Text;
                                jm["level"] = err.Level.ToString();
                                if (err.Level == global::Artech.Common.Diagnostics.MessageLevel.Error) hasErrors = true;
                            }
                            else
                            {
                                jm["text"] = m.ToString();
                            }
                            msgs.Add(jm);
                        }
                    }
                    catch
                    {
                        if (!string.IsNullOrEmpty(output.ErrorText))
                        {
                            msgs.Add(new JObject { ["text"] = output.ErrorText });
                            hasErrors = true;
                        }
                    }
                }

                if (hasErrors)
                {
                    // Phase 6: Self-Healing (Frente 6)
                    var index = _objectService.GetIndex();
                    var healing = HealingService.AttemptHealing(code, msgs, index);
                    if (healing.Healed)
                    {
                        Logger.Info($"[Self-Healing] Retrying save with corrected code for {obj.Name}");
                        sourcePart.Source = healing.NewCode;
                        obj.Save();
                        
                        // Check if retry was successful
                        if (!obj.SaveOutput.HasErrors)
                        {
                            Logger.Info($"[Self-Healing] {obj.Name} saved successfully after correction.");
                            return new JObject { 
                                ["status"] = "Success", 
                                ["healed"] = true, 
                                ["action"] = healing.ActionTaken 
                            }.ToString();
                        }
                        else
                        {
                            Logger.Warn($"[Self-Healing] {obj.Name} still has errors after correction.");
                            // Re-capture messages from failed retry
                            msgs = new JArray();
                            foreach (var m in obj.SaveOutput)
                            {
                                var jm = new JObject();
                                if (m is global::Artech.Common.Diagnostics.OutputError err)
                                {
                                    jm["id"] = err.ErrorCode;
                                    jm["text"] = err.Text;
                                    jm["level"] = err.Level.ToString();
                                }
                                else jm["text"] = m.ToString();
                                msgs.Add(jm);
                            }
                        }
                    }

                    Logger.Warn($"Object {obj.Name} saved with errors.");
                    return new JObject { ["status"] = "Error", ["messages"] = msgs }.ToString();
                }

                if (msgs.Count > 0)
                {
                    Logger.Info($"Object {obj.Name} saved with {msgs.Count} warnings.");
                    return new JObject { ["status"] = "Success", ["messages"] = msgs }.ToString();
                }

                Logger.Info($"Object {obj.Name} saved successfully.");
                return "{\"status\": \"Success\"}";
            }
            catch (Exception ex)
            {
                Logger.Error($"WriteObject Fatal Error on {target}: {ex.Message}");
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private Guid MapLogicalPartToGuid(string objType, string logicalPart)
        {
            string lp = logicalPart.ToLower();
            if (lp == "source" || lp == "code")
            {
                if (objType == "Procedure") return PART_PROCEDURE;
                return PART_EVENTS;
            }
            if (lp == "rules") return PART_RULES;
            if (lp == "events") return PART_EVENTS;
            if (lp == "variables") return PART_VARIABLES;
            if (lp == "webform" || lp == "form" || lp == "layout") return PART_WEB_FORM;
            return PART_PROCEDURE;
        }

        public string WriteSection(string target, string partName, string sectionName, string code)
        {
            return "{\"status\": \"Not implemented yet\"}";
        }
    }
}
