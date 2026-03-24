using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Common.Properties;
using Artech.Genexus.Common.Parts;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class PropertyService
    {
        private readonly ObjectService _objectService;

        public PropertyService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        public string GetProperties(string target, string controlName = null)
        {
            try
            {
                var obj = _objectService.FindObject(target);
                if (obj == null) return Models.McpResponse.Error("Object not found", target);

                dynamic container = obj;
                if (!string.IsNullOrEmpty(controlName))
                {
                    container = FindControl(obj, controlName);
                    if (container == null) return Models.McpResponse.Error($"Control '{controlName}' not found in {obj.Name}", target);
                }

                return SerializeProperties(container).ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string SetProperty(string target, string propName, string value, string controlName = null)
        {
            try
            {
                var obj = _objectService.FindObject(target);
                if (obj == null) return Models.McpResponse.Error("Object not found", target);

                dynamic container = obj;
                if (!string.IsNullOrEmpty(controlName))
                {
                    container = FindControl(obj, controlName);
                    if (container == null) return Models.McpResponse.Error($"Control '{controlName}' not found in {obj.Name}", target);
                }
                
                using (var trans = obj.Model.KB.BeginTransaction())
                {
                    try {
                        container.SetPropertyValue(propName, value);
                    } catch {
                        var pInfo = container.GetType().GetProperty(propName);
                        if (pInfo != null && pInfo.CanWrite) pInfo.SetValue(container, value);
                        else throw new Exception($"Property '{propName}' not found or not writable on {controlName ?? obj.Name}.");
                    }
                    
                    try { if (container != obj) container.Dirty = true; } catch { }
                    obj.EnsureSave();
                    trans.Commit();
                }

                return "{\"status\": \"Success\"}";
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private dynamic FindControl(KBObject obj, string name)
        {
            // Try WebForm
            var webFormPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.TypeDescriptor.Name == "WebForm");
            if (webFormPart != null)
            {
                dynamic dPart = webFormPart;
                try {
                   // Defensive discovery for different SDK versions
                   if (dPart.Form != null) {
                       var ctrl = FindInControlCollection(dPart.Form, name);
                       if (ctrl != null) return ctrl;
                   }
                } catch {}
                
                try {
                   if (dPart.WebForm != null && dPart.WebForm.Form != null) {
                       var ctrl = FindInControlCollection(dPart.WebForm.Form, name);
                       if (ctrl != null) return ctrl;
                   }
                } catch {}
            }

            return null;
        }

        private dynamic FindInControlCollection(dynamic root, string name)
        {
            if (root == null) return null;
            try { if (string.Equals(root.Name, name, StringComparison.OrdinalIgnoreCase)) return root; } catch {}

            try {
                if (root.Controls != null) {
                    foreach (dynamic child in root.Controls) {
                        var found = FindInControlCollection(child, name);
                        if (found != null) return found;
                    }
                }
            } catch {}
            return null;
        }

        private JObject SerializeProperties(dynamic container)
        {
            var result = new JObject();
            var props = new JArray();

            try
            {
                if (container != null && container.Properties != null)
                {
                    foreach (dynamic prop in container.Properties)
                    {
                        try {
                            var pObj = new JObject();
                            pObj["name"] = prop.Name.ToString();
                            pObj["value"] = prop.Value?.ToString() ?? "";
                            
                            try {
                                if (prop.Definition != null) {
                                    pObj["type"] = prop.Definition.Type.ToString();
                                    pObj["readOnly"] = prop.Definition.ReadOnly;
                                }
                            } catch {}

                            props.Add(pObj);
                        } catch { }
                    }
                }
            }
            catch (Exception ex) { Logger.Debug($"General error in SerializeProperties: {ex.Message}"); }

            result["properties"] = props;
            return result;
        }
    }
}
