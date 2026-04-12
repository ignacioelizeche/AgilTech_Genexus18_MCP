using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Structure;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class LayoutService
    {
        private readonly ObjectService _objectService;

        public LayoutService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        public string GetTree(string target, string controlFilter = null, int limit = 500)
        {
            try
            {
                if (limit <= 0) limit = 500;
                if (limit > 2000) limit = 2000;

                var obj = _objectService.FindObject(target);
                if (obj == null)
                {
                    return Models.McpResponse.Error("Object not found", target, "Layout", "The requested object is not available in the active Knowledge Base.");
                }

                var contextResult = LoadVisualContext(obj, target, VisualSurface.Any);
                if (contextResult.Error != null) return contextResult.Error;

                var root = contextResult.Document.Root;
                if (root == null)
                {
                    return Models.McpResponse.Error("Invalid visual XML", target, "Layout", "The visual XML root element is missing.");
                }

                var nodes = new JArray();
                int total = 0;
                int emitted = 0;
                var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                Walk(root, "/" + root.Name.LocalName, nodes, ref total, ref emitted, limit, controlFilter, null, stats);

                var res = new JObject
                {
                    ["n"] = obj.Name,
                    ["t"] = obj.TypeDescriptor.Name,
                    ["s"] = contextResult.Surface.ToString(),
                    ["total"] = total,
                    ["count"] = emitted,
                    ["stats"] = JObject.FromObject(stats),
                    ["nodes"] = nodes,
                    ["empty"] = emitted == 0,
                    ["help"] = new JArray 
                    {
                        "Use genexus_layout(action='set_property', control='ControlName', propertyName='Caption', value='New Value') to modify.",
                        "Use genexus_layout(action='get_preview') to see visual rendering."
                    }
                };

                return res.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string FindControls(string target, string propertyName = null, string query = null, int limit = 200)
        {
            try
            {
                if (limit <= 0) limit = 200;
                if (limit > 2000) limit = 2000;

                var obj = _objectService.FindObject(target);
                if (obj == null)
                {
                    return Models.McpResponse.Error("Object not found", target, "Layout", "The requested object is not available in the active Knowledge Base.");
                }

                var contextResult = LoadVisualContext(obj, target, VisualSurface.Any);
                if (contextResult.Error != null) return contextResult.Error;

                var root = contextResult.Document.Root;
                if (root == null)
                {
                    return Models.McpResponse.Error("Invalid visual XML", target, "Layout", "The visual XML root element is missing.");
                }

                string normalizedProperty = string.IsNullOrWhiteSpace(propertyName) ? null : propertyName;
                string normalizedQuery = string.IsNullOrWhiteSpace(query) ? null : query;

                var nodes = new JArray();
                int total = 0;
                int emitted = 0;
                var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                Walk(root, "/" + root.Name.LocalName, nodes, ref total, ref emitted, limit, null, new FindCriteria
                {
                    PropertyName = normalizedProperty,
                    Query = normalizedQuery
                }, stats);

                var result = new JObject
                {
                    ["n"] = obj.Name,
                    ["t"] = obj.TypeDescriptor.Name,
                    ["s"] = contextResult.Surface.ToString(),
                    ["total"] = total,
                    ["count"] = emitted,
                    ["stats"] = JObject.FromObject(stats),
                    ["nodes"] = nodes,
                    ["empty"] = emitted == 0,
                    ["help"] = new JArray 
                    {
                        "Use genexus_layout(action='set_property', control='ControlName', propertyName='Caption', value='New Value') to modify.",
                        "Use genexus_layout(action='get_preview') to see visual rendering of the layout."
                    }
                };
                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string SetProperty(string target, string controlName, string propertyName, string value)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(controlName))
                    return Models.McpResponse.Error("Missing control name", target, "Layout", "Provide 'control' with the visual control identifier.");
                if (string.IsNullOrWhiteSpace(propertyName))
                    return Models.McpResponse.Error("Missing property name", target, "Layout", "Provide 'propertyName' for the visual mutation.");

                var obj = _objectService.FindObject(target);
                if (obj == null)
                    return Models.McpResponse.Error("Object not found", target, "Layout", "The requested object is not available in the active Knowledge Base.");

                var contextResult = LoadVisualContext(obj, target, VisualSurface.Any);
                if (contextResult.Error != null) return contextResult.Error;

                var doc = contextResult.Document;
                var element = FindControlElement(doc, controlName);
                if (element == null)
                    return Models.McpResponse.Error("Control not found", target, "Layout", "No visual node matched control '" + controlName + "'.");

                string attrName;
                string previous;
                if (IsTextPropertyName(propertyName))
                {
                    attrName = "InnerText";
                    previous = element.Value;
                    element.Value = value ?? string.Empty;
                }
                else
                {
                    attrName = ResolveCanonicalAttributeName(element, propertyName);
                    previous = element.Attribute(attrName) != null ? element.Attribute(attrName).Value : null;
                    element.SetAttributeValue(attrName, value ?? string.Empty);
                }

                string normalized = doc.ToString();
                Logger.Info($"SetProperty: Target XML updated for {controlName}. attrName={attrName}. Current element attributes: {string.Join(", ", System.Linq.Enumerable.Select(element.Attributes(), a => a.Name.LocalName + "=" + a.Value))}");
                Logger.Info($"SetProperty: New XML Sample (first 500 chars): " + (normalized.Length > 500 ? normalized.Substring(0, 500) : normalized));
                
                var persistError = PersistVisualXml(obj, contextResult, target, normalized);
                if (persistError != null) return persistError;

                var persistedObject = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target);
                var persistedContext = LoadVisualContext(persistedObject ?? obj, target, VisualSurface.Any);
                if (persistedContext.Error != null) return persistedContext.Error;

                var persistedElement = FindControlElement(persistedContext.Document, controlName);
                if (persistedElement == null)
                    return Models.McpResponse.Error("Layout read-back failed", target, "Layout", "Control was not found after save.");

                string persistedValue = string.Equals(attrName, "InnerText", StringComparison.Ordinal)
                    ? persistedElement.Value
                    : (persistedElement.Attribute(attrName) != null ? persistedElement.Attribute(attrName).Value : null);
                
                bool match = IsPersistedValueMatch(attrName, value, persistedValue);
                bool isProcedure = string.Equals(obj.TypeDescriptor?.Name, "Procedure", StringComparison.OrdinalIgnoreCase);

                if (!match)
                {
                    if (isProcedure)
                    {
                        // Reports can defer SDK persistence. Retry a few read-backs before failing.
                        for (int attempt = 0; attempt < 6 && !match; attempt++)
                        {
                            System.Threading.Thread.Sleep(350);
                            var retryObject = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target) ?? obj;
                            var retryContext = LoadVisualContext(retryObject, target, VisualSurface.Any);
                            if (retryContext.Error != null) break;

                            var retryElement = FindControlElement(retryContext.Document, controlName);
                            if (retryElement == null) break;

                            persistedValue = string.Equals(attrName, "InnerText", StringComparison.Ordinal)
                                ? retryElement.Value
                                : (retryElement.Attribute(attrName) != null ? retryElement.Attribute(attrName).Value : null);
                            match = IsPersistedValueMatch(attrName, value, persistedValue);
                        }
                    }
                    if (!match)
                    {
                        return Models.McpResponse.Error(
                            "Layout write verification failed",
                            target,
                            "Layout",
                            "Persisted value does not match requested value after SDK save and read-back.");
                    }
                }

                var result = new JObject
                {
                    ["status"] = "Success",
                    ["name"] = obj.Name,
                    ["surface"] = contextResult.Surface.ToString(),
                    ["control"] = controlName,
                    ["propertyName"] = attrName,
                    ["previousValue"] = previous,
                    ["value"] = persistedValue
                };
                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string GetVisualPreview(string target)
        {
            try
            {
                var obj = _objectService.FindObject(target);
                if (obj == null)
                {
                    return Models.McpResponse.Error("Object not found", target, "Layout", "The requested object is not available in the active Knowledge Base.");
                }

                var contextResult = LoadVisualContext(obj, target, VisualSurface.Any);
                if (contextResult.Error != null) return contextResult.Error;

                var snapshotService = new VisualSnapshotService();
                string base64 = snapshotService.GetSnapshotBase64(contextResult.Document.ToString());

                return new JObject
                {
                    ["name"] = obj.Name,
                    ["type"] = obj.TypeDescriptor.Name,
                    ["surface"] = contextResult.Surface.ToString(),
                    ["snapshot"] = base64
                }.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string SetProperties(string target, JArray changes)
        {
            try
            {
                if (changes == null || changes.Count == 0)
                    return Models.McpResponse.Error("Missing changes", target, "Layout", "Provide 'changes' with at least one mutation item.");

                var obj = _objectService.FindObject(target);
                if (obj == null)
                    return Models.McpResponse.Error("Object not found", target, "Layout", "The requested object is not available in the active Knowledge Base.");

                var contextResult = LoadVisualContext(obj, target, VisualSurface.Any);
                if (contextResult.Error != null) return contextResult.Error;

                var doc = contextResult.Document;
                var applied = new JArray();

                foreach (var token in changes)
                {
                    var change = token as JObject;
                    if (change == null) continue;

                    string controlName = change["control"]?.ToString();
                    string propertyName = change["propertyName"]?.ToString();
                    string value = change["value"]?.ToString();

                    if (string.IsNullOrWhiteSpace(controlName) || string.IsNullOrWhiteSpace(propertyName))
                    {
                        return Models.McpResponse.Error("Invalid change entry", target, "Layout", "Each change item requires 'control' and 'propertyName'.");
                    }

                    var element = FindControlElement(doc, controlName);
                    if (element == null)
                    {
                        return Models.McpResponse.Error("Control not found", target, "Layout", "No visual node matched control '" + controlName + "'.");
                    }

                    string attrName;
                    string previous;
                    if (IsTextPropertyName(propertyName))
                    {
                        attrName = "InnerText";
                        previous = element.Value;
                        element.Value = value ?? string.Empty;
                    }
                    else
                    {
                        attrName = ResolveCanonicalAttributeName(element, propertyName);
                        previous = element.Attribute(attrName) != null ? element.Attribute(attrName).Value : null;
                        element.SetAttributeValue(attrName, value ?? string.Empty);
                    }

                    applied.Add(new JObject
                    {
                        ["control"] = controlName,
                        ["propertyName"] = attrName,
                        ["previousValue"] = previous,
                        ["value"] = value ?? string.Empty
                    });
                }

                string normalized = doc.ToString();
                var persistError = PersistVisualXml(obj, contextResult, target, normalized);
                if (persistError != null) return persistError;

                var persistedObject = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target);
                var persistedContext = LoadVisualContext(persistedObject ?? obj, target, VisualSurface.Any);
                if (persistedContext.Error != null) return persistedContext.Error;

                foreach (var token in applied)
                {
                    var appliedItem = token as JObject;
                    if (appliedItem == null) continue;

                    string controlName = appliedItem["control"]?.ToString();
                    string attrName = appliedItem["propertyName"]?.ToString();
                    string expected = appliedItem["value"]?.ToString() ?? string.Empty;

                    var persistedEl = FindControlElement(persistedContext.Document, controlName);
                    if (persistedEl == null)
                        return Models.McpResponse.Error("Layout read-back failed", target, "Layout", "Control '" + controlName + "' was not found after save.");

                    string actual = string.Equals(attrName, "InnerText", StringComparison.Ordinal)
                        ? (persistedEl.Value ?? string.Empty)
                        : (persistedEl.Attribute(attrName) != null ? persistedEl.Attribute(attrName).Value : string.Empty);
                    if (!IsPersistedValueMatch(attrName, expected, actual))
                    {
                        return Models.McpResponse.Error("Layout write verification failed", target, "Layout", "Persisted value for control '" + controlName + "' and property '" + attrName + "' does not match requested value.");
                    }
                }

                return new JObject
                {
                    ["status"] = "Success",
                    ["name"] = obj.Name,
                    ["surface"] = contextResult.Surface.ToString(),
                    ["applied"] = applied,
                    ["count"] = applied.Count
                }.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string RenamePrintBlock(string target, string currentName, string newName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(currentName) || string.IsNullOrWhiteSpace(newName))
                {
                    return Models.McpResponse.Error("Missing print block names", target, "Layout", "Provide 'currentName' and 'newName'.");
                }

                var obj = _objectService.FindObject(target);
                if (obj == null)
                {
                    return Models.McpResponse.Error("Object not found", target, "Layout", "The requested object is not available in the active Knowledge Base.");
                }

                var context = LoadVisualContext(obj, target, VisualSurface.Report);
                if (context.Error != null) return context.Error;
                if (context.VisualPart == null)
                {
                    return Models.McpResponse.Error("Report part not found", target, "Layout", "No report layout part was available for this Procedure.");
                }

                var kb = _objectService.GetKbService().GetKB();
                if (kb == null)
                {
                    return Models.McpResponse.Error("KB not opened", target, "Layout", "Open a Knowledge Base before mutating report layout.");
                }

                string sourceSnapshot = GetProcedureSourceSnapshot(obj);

                using (var tx = kb.BeginTransaction())
                {
                    try
                    {
                        if (!TryNormalizeReportPrintCommandsInSourceInMemory(obj, context.Document.ToString(), out string normalizeError))
                        {
                            tx.Rollback();
                            return Models.McpResponse.Error("Rename print block source sync failed", target, "Layout", normalizeError);
                        }

                        if (!ReportLayoutHelper.RenamePrintBlock(context.VisualPart, currentName, newName, persist: false))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Error("Rename print block failed", target, "Layout", "The SDK could not stage the print block rename operation.");
                        }

                        if (!TryRenamePrintCommandInSourceInMemory(obj, currentName, newName, out string sourcePrepareError))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Error("Rename print block source sync failed", target, "Layout", sourcePrepareError);
                        }

                        if (!TrySaveVisualPart(context.VisualPart, out string partSaveError))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Error("Rename print block failed", target, "Layout", partSaveError);
                        }

                        obj.EnsureSave(true);
                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        TryRestoreProcedureSource(obj, sourceSnapshot);
                        tx.Rollback();
                        return Models.McpResponse.Error("Rename print block failed", target, "Layout", ex.Message);
                    }
                }
                _objectService.MarkReadCacheDirty(obj, "Layout");

                var refreshedObj = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target) ?? obj;
                var refreshed = LoadVisualContext(refreshedObj, target, VisualSurface.Report);
                if (refreshed.Error != null) return refreshed.Error;

                bool exists = refreshed.Document.Descendants("PrintBlock")
                    .Any(pb => string.Equals(Attr(pb, "Name"), newName, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(Attr(pb, "ControlName"), newName, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    for (int attempt = 0; attempt < 20 && !exists; attempt++)
                    {
                        System.Threading.Thread.Sleep(500);
                        var retryObj = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target) ?? obj;
                        var retry = LoadVisualContext(retryObj, target, VisualSurface.Report);
                        if (retry.Error != null) break;
                        exists = retry.Document.Descendants("PrintBlock")
                            .Any(pb => string.Equals(Attr(pb, "Name"), newName, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(Attr(pb, "ControlName"), newName, StringComparison.OrdinalIgnoreCase));
                    }
                }
                if (!exists)
                {
                    var healObj = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? obj;
                    var healContext = LoadVisualContext(healObj, target, VisualSurface.Report);
                    if (healContext.Error == null && healContext.Document != null)
                    {
                        if (TryNormalizeReportPrintCommandsInSourceInMemory(healObj, healContext.Document.ToString(), out _))
                        {
                            TryFlushSourceForLayoutMutation(healObj, out _);
                        }
                    }

                    return Models.McpResponse.Error("Rename print block verification failed", target, "Layout", "Print block rename was not found in persisted report XML.");
                }

                return new JObject
                {
                    ["status"] = "Success",
                    ["name"] = obj.Name,
                    ["operation"] = "RenamePrintBlock",
                    ["currentName"] = currentName,
                    ["newName"] = newName
                }.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string AddPrintBlock(string target, string printBlockName, int? height)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(printBlockName))
                {
                    return Models.McpResponse.Error("Missing print block name", target, "Layout", "Provide 'printBlockName'.");
                }

                var obj = _objectService.FindObject(target);
                if (obj == null)
                {
                    return Models.McpResponse.Error("Object not found", target, "Layout", "The requested object is not available in the active Knowledge Base.");
                }

                var context = LoadVisualContext(obj, target, VisualSurface.Report);
                if (context.Error != null) return context.Error;
                if (context.VisualPart == null)
                {
                    return Models.McpResponse.Error("Report part not found", target, "Layout", "No report layout part was available for this Procedure.");
                }

                var kb = _objectService.GetKbService().GetKB();
                if (kb == null)
                {
                    return Models.McpResponse.Error("KB not opened", target, "Layout", "Open a Knowledge Base before mutating report layout.");
                }

                string sourceSnapshot = GetProcedureSourceSnapshot(obj);

                using (var tx = kb.BeginTransaction())
                {
                    try
                    {
                        if (!TryNormalizeReportPrintCommandsInSourceInMemory(obj, context.Document.ToString(), out string normalizeError))
                        {
                            tx.Rollback();
                            return Models.McpResponse.Error("Add print block source sync failed", target, "Layout", normalizeError);
                        }

                        if (!ReportLayoutHelper.AddPrintBlock(context.VisualPart, printBlockName, height, persist: false))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Error("Add print block failed", target, "Layout", "The SDK could not stage the new print block.");
                        }

                        if (!TryInsertPrintCommandInSourceInMemory(obj, printBlockName, out string sourcePrepareError))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Error("Add print block source sync failed", target, "Layout", sourcePrepareError);
                        }

                        if (!TrySaveVisualPart(context.VisualPart, out string partSaveError))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Error("Add print block failed", target, "Layout", partSaveError);
                        }

                        obj.EnsureSave(true);
                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        TryRestoreProcedureSource(obj, sourceSnapshot);
                        tx.Rollback();
                        return Models.McpResponse.Error("Add print block failed", target, "Layout", ex.Message);
                    }
                }
                _objectService.MarkReadCacheDirty(obj, "Layout");

                var refreshedObj = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target) ?? obj;
                var refreshed = LoadVisualContext(refreshedObj, target, VisualSurface.Report);
                if (refreshed.Error != null) return refreshed.Error;

                var added = refreshed.Document.Descendants("PrintBlock")
                    .FirstOrDefault(pb => string.Equals(Attr(pb, "Name"), printBlockName, StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(Attr(pb, "ControlName"), printBlockName, StringComparison.OrdinalIgnoreCase));
                if (added == null)
                {
                    for (int attempt = 0; attempt < 20 && added == null; attempt++)
                    {
                        System.Threading.Thread.Sleep(500);
                        var retryObj = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target) ?? obj;
                        var retry = LoadVisualContext(retryObj, target, VisualSurface.Report);
                        if (retry.Error != null) break;
                        added = retry.Document.Descendants("PrintBlock")
                            .FirstOrDefault(pb => string.Equals(Attr(pb, "Name"), printBlockName, StringComparison.OrdinalIgnoreCase) ||
                                                  string.Equals(Attr(pb, "ControlName"), printBlockName, StringComparison.OrdinalIgnoreCase));
                    }
                }
                if (added == null)
                {
                    var healObj = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? obj;
                    var healContext = LoadVisualContext(healObj, target, VisualSurface.Report);
                    if (healContext.Error == null && healContext.Document != null)
                    {
                        if (TryNormalizeReportPrintCommandsInSourceInMemory(healObj, healContext.Document.ToString(), out _))
                        {
                            TryFlushSourceForLayoutMutation(healObj, out _);
                        }
                    }

                    return Models.McpResponse.Error("Add print block verification failed", target, "Layout", "New print block was not found in persisted report XML.");
                }

                return new JObject
                {
                    ["status"] = "Success",
                    ["name"] = obj.Name,
                    ["operation"] = "AddPrintBlock",
                    ["printBlockName"] = printBlockName,
                    ["height"] = Attr(added, "Height")
                }.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string InspectSurface(string target, int limit = 50)
        {
            try
            {
                var obj = _objectService.FindObject(target);
                if (obj == null)
                {
                    return Models.McpResponse.Error("Object not found", target, "Layout", "The requested object is not available in the active Knowledge Base.");
                }

                var parts = new[] { "Layout", "PatternVirtual", "WebForm" };
                var surfaces = new JArray();
                var partsCatalog = new JArray();

                foreach (KBObjectPart p in obj.Parts)
                {
                    partsCatalog.Add(new JObject
                    {
                        ["name"] = p.TypeDescriptor?.Name ?? p.GetType().Name,
                        ["guid"] = p.Type.ToString(),
                        ["type"] = p.GetType().FullName,
                        ["isSource"] = p is ISource
                    });
                }

                int totalCandidates = 0;

                foreach (var partName in parts)
                {
                    var part = PartAccessor.GetPart(obj, partName);
                    if (part == null) continue;

                    var partInfo = new JObject
                    {
                        ["part"] = partName,
                        ["type"] = part.GetType().FullName,
                        ["isSource"] = part is ISource
                    };

                    var xmlCandidates = new JArray();
                    var candidatesCollected = CollectXmlCandidates(part, includeNonPublic: true, includeNested: true);
                    totalCandidates += candidatesCollected.Count;

                    foreach (var candidate in candidatesCollected.OrderByDescending(c => c.Score).Take(limit))
                    {
                        xmlCandidates.Add(new JObject
                        {
                            ["member"] = candidate.MemberName,
                            ["sourcePath"] = candidate.SourcePath,
                            ["kind"] = candidate.MemberKind,
                            ["writable"] = candidate.MemberWritable,
                            ["depth"] = candidate.Depth,
                            ["score"] = candidate.Score,
                            ["root"] = candidate.Document?.Root?.Name.LocalName,
                            ["nodes"] = candidate.Document?.Descendants().Count() ?? 0,
                            ["controlAttrs"] = candidate.Document?.Descendants().Count(e => e.Attribute("ControlName") != null) ?? 0
                        });
                    }

                    partInfo["candidatesCount"] = candidatesCollected.Count;
                    partInfo["candidatesReturned"] = xmlCandidates.Count;
                    partInfo["xmlCandidates"] = xmlCandidates;
                    surfaces.Add(partInfo);
                }

                bool isEmpty = surfaces.Count == 0;

                var resultObj = new JObject();
                resultObj["name"] = obj.Name;
                resultObj["type"] = obj.TypeDescriptor.Name;
                resultObj["empty"] = isEmpty;
                resultObj["totalSurfaces"] = surfaces.Count;
                resultObj["totalParts"] = partsCatalog.Count;
                resultObj["totalCandidates"] = totalCandidates;
                resultObj["partsCatalog"] = partsCatalog;
                resultObj["surfaces"] = surfaces;
                
                if (isEmpty)
                {
                    resultObj["help"] = "No structural definitions found. Object lacks supported visual XML parts.";
                }
                else if (totalCandidates > limit)
                {
                    resultObj["help"] = $"Output truncated ({limit} out of {totalCandidates} candidates shown per surface).";
                }

                return resultObj.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string ScanMutators(string target, int limit = 100)
        {
            try
            {
                if (limit <= 0) limit = 100;
                if (limit > 500) limit = 500;

                var obj = _objectService.FindObject(target);
                if (obj == null)
                {
                    return Models.McpResponse.Error("Object not found", target, "Layout", "The requested object is not available in the active Knowledge Base.");
                }

                var partNames = new[] { "Layout", "PatternVirtual", "WebForm" };
                var results = new JArray();
                int totalMutators = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                const long budgetMs = 15000;
                bool timedOut = false;

                foreach (var partName in partNames)
                {
                    if (sw.ElapsedMilliseconds > budgetMs) { timedOut = true; break; }

                    var part = PartAccessor.GetPart(obj, partName);
                    if (part == null) continue;

                    var partResult = new JObject
                    {
                        ["part"] = partName,
                        ["partType"] = part.GetType().FullName
                    };

                    var mutators = new JArray();
                    var visited = new HashSet<object>(ReferenceObjectComparer.Instance);
                    ScanObjectMutators(part, part.GetType().Name, 0, 2, mutators, visited, sw, budgetMs);

                    totalMutators += mutators.Count;

                    var sortedMutators = mutators
                        .Cast<JObject>()
                        .OrderByDescending(m => (int)(m["relevance"] ?? 0))
                        .Take(limit)
                        .ToList();

                    var limitedMutators = new JArray();
                    foreach (var m in sortedMutators) limitedMutators.Add(m);

                    partResult["mutatorsTotal"] = mutators.Count;
                    partResult["mutatorsReturned"] = limitedMutators.Count;
                    partResult["mutators"] = limitedMutators;
                    results.Add(partResult);
                }

                bool isEmpty = totalMutators == 0;

                var resultObj = new JObject();
                resultObj["name"] = obj.Name;
                resultObj["type"] = obj.TypeDescriptor.Name;
                resultObj["empty"] = isEmpty;
                resultObj["totalMutators"] = totalMutators;
                resultObj["timedOut"] = timedOut;
                resultObj["elapsedMs"] = sw.ElapsedMilliseconds;
                resultObj["surfaces"] = results;

                if (timedOut)
                {
                    resultObj["help"] = $"Scan aborted after {budgetMs}ms budget. Partial results returned ({totalMutators} endpoints found before timeout).";
                }
                else if (isEmpty)
                {
                    resultObj["help"] = "0 mutation endpoints found. The SDK does not expose writable control paths for this object type.";
                }
                else
                {
                    resultObj["help"] = $"Found {totalMutators} mutation endpoint(s). Look for 'writable_property' and 'setter_method' kinds with high relevance scores for persistent mutation candidates.";
                }

                return resultObj.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private static readonly HashSet<string> DangerousTypeFragments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "KnowledgeBase", "Model", "DesignModel", "EntityManager", "BLServices",
            "Transaction", "DataStore", "Environment", "GxModel", "KBModule",
            "Artech.Architecture.Common.Services", "Artech.Architecture.BL",
            "Artech.Common.Framework", "Artech.Architecture.Common.Descriptors"
        };

        private static bool IsDangerousTraversalType(Type type)
        {
            if (type == null) return true;
            string fullName = type.FullName ?? type.Name ?? "";
            foreach (var frag in DangerousTypeFragments)
            {
                if (fullName.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }


        private static void ScanObjectMutators(
            object instance,
            string sourcePath,
            int depth,
            int maxDepth,
            JArray mutators,
            HashSet<object> visited,
            System.Diagnostics.Stopwatch sw,
            long budgetMs)
        {
            if (instance == null || depth > maxDepth) return;
            if (sw.ElapsedMilliseconds > budgetMs) return;
            if (!visited.Add(instance)) return;

            var type = instance.GetType();
            if (IsDangerousTraversalType(type)) return;

            // At depth 0/1, scan the actual instance members.
            // For nested traversal, switch to static type-only scanning to avoid COM deadlocks.
            ScanTypeMembers(type, sourcePath, depth, maxDepth, mutators, sw, budgetMs, new HashSet<string>());
        }

        /// <summary>
        /// Pure metadata scan — enumerates members by Type reflection only, never calls GetValue.
        /// Safe against COM STA deadlocks.
        /// </summary>
        private static void ScanTypeMembers(
            Type type,
            string sourcePath,
            int depth,
            int maxDepth,
            JArray mutators,
            System.Diagnostics.Stopwatch sw,
            long budgetMs,
            HashSet<string> visitedTypes)
        {
            if (type == null || depth > maxDepth) return;
            if (sw.ElapsedMilliseconds > budgetMs) return;

            string typeKey = type.FullName ?? type.Name;
            if (!visitedTypes.Add(typeKey)) return;
            if (IsDangerousTraversalType(type)) return;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            // 1. Writable properties
            foreach (var prop in type.GetProperties(flags))
            {
                if (sw.ElapsedMilliseconds > budgetMs) return;
                if (prop.GetIndexParameters().Length > 0) continue;

                bool canRead = prop.CanRead && prop.GetMethod != null;
                bool canWrite = prop.CanWrite && prop.SetMethod != null;

                string propPath = sourcePath + "." + prop.Name;

                if (canWrite)
                {
                    int relevance = ScoreMutatorRelevance(prop.Name, prop.PropertyType, "property");
                    if (relevance > 0 || depth <= 1)
                    {
                        mutators.Add(new JObject
                        {
                            ["kind"] = prop.SetMethod.IsPublic ? "writable_property" : "writable_property_nonpublic",
                            ["path"] = propPath,
                            ["depth"] = depth,
                            ["valueType"] = prop.PropertyType.Name,
                            ["valueTypeFullName"] = prop.PropertyType.FullName,
                            ["getterPublic"] = canRead && prop.GetMethod.IsPublic,
                            ["setterPublic"] = prop.SetMethod.IsPublic,
                            ["relevance"] = relevance
                        });
                    }
                }

                // Traverse into nested type (static only — no GetValue)
                if (canRead && depth < maxDepth && ShouldTraverseMutatorTarget(prop.Name, prop.PropertyType))
                {
                    if (!IsDangerousTraversalType(prop.PropertyType))
                    {
                        ScanTypeMembers(prop.PropertyType, propPath, depth + 1, maxDepth, mutators, sw, budgetMs, visitedTypes);
                    }
                }
            }

            // 2. Methods that accept parameters (potential mutators)
            foreach (var method in type.GetMethods(flags))
            {
                if (sw.ElapsedMilliseconds > budgetMs) return;
                if (method.IsSpecialName) continue;
                if (method.DeclaringType == typeof(object)) continue;

                var parameters = method.GetParameters();
                string methodPath = sourcePath + "." + method.Name + "(" + string.Join(", ", parameters.Select(p => p.ParameterType.Name)) + ")";
                int relevance = ScoreMutatorRelevance(method.Name, method.ReturnType, "method");

                // Setter methods (accept 1+ params)
                if (parameters.Length >= 1 && parameters.Length <= 4)
                {
                    string mName = method.Name.ToLowerInvariant();
                    bool looksLikeMutator =
                        mName.StartsWith("set", StringComparison.Ordinal) ||
                        mName.StartsWith("add", StringComparison.Ordinal) ||
                        mName.StartsWith("remove", StringComparison.Ordinal) ||
                        mName.StartsWith("insert", StringComparison.Ordinal) ||
                        mName.StartsWith("delete", StringComparison.Ordinal) ||
                        mName.StartsWith("update", StringComparison.Ordinal) ||
                        mName.StartsWith("apply", StringComparison.Ordinal) ||
                        mName.StartsWith("load", StringComparison.Ordinal) ||
                        mName.StartsWith("deserialize", StringComparison.Ordinal) ||
                        mName.StartsWith("clear", StringComparison.Ordinal) ||
                        mName.StartsWith("move", StringComparison.Ordinal) ||
                        mName.StartsWith("replace", StringComparison.Ordinal) ||
                        mName.StartsWith("create", StringComparison.Ordinal) ||
                        mName.Contains("control") ||
                        mName.Contains("layout") ||
                        mName.Contains("xml") ||
                        mName.Contains("form");

                    if (looksLikeMutator || relevance > 10)
                    {
                        var paramArray = new JArray();
                        foreach (var p in parameters)
                        {
                            paramArray.Add(new JObject
                            {
                                ["name"] = p.Name,
                                ["type"] = p.ParameterType.Name,
                                ["typeFullName"] = p.ParameterType.FullName,
                                ["isOptional"] = p.IsOptional
                            });
                        }

                        mutators.Add(new JObject
                        {
                            ["kind"] = method.IsPublic ? "setter_method" : "setter_method_nonpublic",
                            ["path"] = methodPath,
                            ["depth"] = depth,
                            ["returnType"] = method.ReturnType.Name,
                            ["isPublic"] = method.IsPublic,
                            ["parameters"] = paramArray,
                            ["relevance"] = relevance + (looksLikeMutator ? 20 : 0)
                        });
                    }
                }

                // Parameterless methods returning collections
                if (parameters.Length == 0 && relevance > 5)
                {
                    bool returnsCollection =
                        typeof(IEnumerable).IsAssignableFrom(method.ReturnType) &&
                        method.ReturnType != typeof(string) &&
                        method.ReturnType != typeof(byte[]);

                    if (returnsCollection)
                    {
                        mutators.Add(new JObject
                        {
                            ["kind"] = method.IsPublic ? "collection_accessor" : "collection_accessor_nonpublic",
                            ["path"] = methodPath,
                            ["depth"] = depth,
                            ["returnType"] = method.ReturnType.Name,
                            ["returnTypeFullName"] = method.ReturnType.FullName,
                            ["isPublic"] = method.IsPublic,
                            ["relevance"] = relevance + 15
                        });
                    }
                }
            }

            // 3. Collection properties (IList, ICollection patterns) — metadata only, no GetValue
            foreach (var prop in type.GetProperties(flags))
            {
                if (sw.ElapsedMilliseconds > budgetMs) return;
                if (prop.GetIndexParameters().Length > 0 || !prop.CanRead) continue;

                bool returnsCollection =
                    typeof(IEnumerable).IsAssignableFrom(prop.PropertyType) &&
                    prop.PropertyType != typeof(string) &&
                    prop.PropertyType != typeof(byte[]);

                if (!returnsCollection) continue;

                string propPath = sourcePath + "." + prop.Name;
                int relevance = ScoreMutatorRelevance(prop.Name, prop.PropertyType, "collection");

                if (relevance > 0 || depth <= 1)
                {
                    // Check if the collection type has Add/Remove methods — metadata-only, no invocation
                    var collectionMethods = prop.PropertyType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(m => m.Name == "Add" || m.Name == "Remove" || m.Name == "Insert" || m.Name == "Clear" || m.Name == "RemoveAt")
                        .Select(m => m.Name)
                        .Distinct()
                        .ToArray();

                    mutators.Add(new JObject
                    {
                        ["kind"] = "collection_property",
                        ["path"] = propPath,
                        ["depth"] = depth,
                        ["collectionType"] = prop.PropertyType.Name,
                        ["collectionTypeFullName"] = prop.PropertyType.FullName,
                        ["isPublic"] = prop.GetMethod?.IsPublic ?? false,
                        ["mutationMethods"] = new JArray(collectionMethods),
                        ["relevance"] = relevance + (collectionMethods.Length > 0 ? 25 : 0)
                    });
                }
            }
        }

        private static int ScoreMutatorRelevance(string name, Type type, string category)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;

            string n = name.ToLowerInvariant();
            string t = (type?.FullName ?? type?.Name ?? "").ToLowerInvariant();
            int score = 0;

            // Name-based scoring
            if (n.Contains("control")) score += 40;
            if (n.Contains("layout")) score += 35;
            if (n.Contains("xml")) score += 30;
            if (n.Contains("form")) score += 25;
            if (n.Contains("source")) score += 20;
            if (n.Contains("caption")) score += 15;
            if (n.Contains("visible")) score += 15;
            if (n.Contains("style")) score += 10;
            if (n.Contains("class")) score += 10;
            if (n.Contains("position")) score += 10;
            if (n.Contains("size")) score += 8;
            if (n.Contains("width") || n.Contains("height")) score += 8;
            if (n.Contains("row") || n.Contains("column") || n.Contains("cell")) score += 12;
            if (n.Contains("table")) score += 15;
            if (n.Contains("report")) score += 12;
            if (n.Contains("printblock")) score += 20;
            if (n.Contains("band")) score += 15;
            if (n.Contains("attribute")) score += 10;
            if (n.Contains("variable")) score += 10;
            if (n.Contains("metadata")) score += 8;
            if (n.Contains("serialize") || n.Contains("deserialize")) score += 25;
            if (n.Contains("load") || n.Contains("apply")) score += 15;

            // Type-based scoring
            if (t.Contains("artech.genexus")) score += 15;
            if (t.Contains("layout")) score += 15;
            if (t.Contains("control")) score += 15;
            if (t.Contains("form")) score += 10;
            if (t.Contains("reportband") || t.Contains("printblock")) score += 20;

            // Penalty for obvious noise
            if (n.StartsWith("get_", StringComparison.Ordinal) && category == "method") score -= 10;
            if (n == "tostring" || n == "gethashcode" || n == "equals" || n == "gettype") score = 0;

            return Math.Max(score, 0);
        }

        private static bool ShouldTraverseMutatorTarget(string memberName, Type memberType)
        {
            if (memberType == null) return false;
            if (memberType == typeof(string)) return false;
            if (memberType.IsPrimitive || memberType.IsEnum) return false;
            if (memberType == typeof(Guid) || memberType == typeof(DateTime)) return false;

            string typeName = (memberType.FullName ?? memberType.Name ?? "").ToLowerInvariant();
            string lowerName = (memberName ?? "").ToLowerInvariant();

            bool strongHint =
                lowerName.Contains("layout") ||
                lowerName.Contains("form") ||
                lowerName.Contains("xml") ||
                lowerName.Contains("control") ||
                lowerName.Contains("meta") ||
                lowerName.Contains("report") ||
                lowerName.Contains("band") ||
                lowerName.Contains("printblock") ||
                typeName.Contains("layout") ||
                typeName.Contains("form") ||
                typeName.Contains("control") ||
                typeName.Contains("report") ||
                typeName.Contains("artech.genexus");

            return strongHint;
        }

        private static void Walk(XElement current, string path, JArray nodes, ref int total, ref int emitted, int limit, string controlFilter, FindCriteria findCriteria, Dictionary<string, int> stats)
        {
            if (current == null) return;
            total++;

            string tag = current.Name.LocalName;
            if (stats != null)
            {
                stats[tag] = stats.TryGetValue(tag, out int currentCount) ? currentCount + 1 : 1;
            }

            string controlName = Attr(current, "ControlName");
            bool matchesByControl = string.IsNullOrWhiteSpace(controlFilter) ||
                                    string.Equals(controlName, controlFilter, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(Attr(current, "InternalName"), controlFilter, StringComparison.OrdinalIgnoreCase);
            bool matchesByCriteria = MatchesCriteria(current, findCriteria);
            bool matches = matchesByControl && matchesByCriteria;

            if (matches && emitted < limit)
            {
                var node = new JObject
                {
                    ["p"] = path,
                    ["t"] = tag,
                    ["n"] = controlName,
                    ["c"] = Attr(current, "Caption"),
                    ["k"] = Attr(current, "Class"),
                    ["v"] = Attr(current, "Attribute") ?? Attr(current, "Variable")
                };
                nodes.Add(node);
                emitted++;
            }

            int idx = 0;
            foreach (var child in current.Elements())
            {
                idx++;
                Walk(child, path + "/" + child.Name.LocalName + "[" + idx + "]", nodes, ref total, ref emitted, limit, controlFilter, findCriteria, stats);
            }
        }

        private static bool MatchesCriteria(XElement element, FindCriteria criteria)
        {
            if (criteria == null) return true;
            if (string.IsNullOrWhiteSpace(criteria.PropertyName) && string.IsNullOrWhiteSpace(criteria.Query)) return true;

            string searchValue;
            if (!string.IsNullOrWhiteSpace(criteria.PropertyName))
            {
                if (IsTextPropertyName(criteria.PropertyName))
                {
                    searchValue = element.Value;
                }
                else
                {
                    var resolved = ResolveCanonicalAttributeName(element, criteria.PropertyName);
                    searchValue = Attr(element, resolved);
                }
            }
            else
            {
                searchValue = string.Join(" ",
                    Attr(element, "ControlName"),
                    Attr(element, "InternalName"),
                    Attr(element, "Caption"),
                    Attr(element, "Class"),
                    Attr(element, "Attribute"),
                    Attr(element, "Variable"),
                    element.Name.LocalName);
            }

            if (string.IsNullOrWhiteSpace(criteria.Query)) return true;
            return (searchValue ?? string.Empty).IndexOf(criteria.Query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static XElement FindControlElement(XDocument doc, string controlName)
        {
            if (string.IsNullOrWhiteSpace(controlName)) return null;

            if (controlName.StartsWith("/", StringComparison.Ordinal))
            {
                return FindElementByPath(doc, controlName);
            }

            return doc
                .Descendants()
                .FirstOrDefault(el =>
                    string.Equals(Attr(el, "ControlName"), controlName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Attr(el, "InternalName"), controlName, StringComparison.OrdinalIgnoreCase));
        }

        private static XElement FindElementByPath(XDocument doc, string path)
        {
            if (doc?.Root == null || string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal))
            {
                return null;
            }

            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return null;

            XElement current = doc.Root;
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                string name = segment;
                int index = 1;

                int idxStart = segment.LastIndexOf('[');
                int idxEnd = segment.LastIndexOf(']');
                if (idxStart > 0 && idxEnd > idxStart)
                {
                    name = segment.Substring(0, idxStart);
                    int parsedIndex;
                    if (int.TryParse(segment.Substring(idxStart + 1, idxEnd - idxStart - 1), out parsedIndex) && parsedIndex > 0)
                    {
                        index = parsedIndex;
                    }
                }

                if (i == 0)
                {
                    if (!string.Equals(current.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }
                    continue;
                }

                var byName = current.Elements(name).ElementAtOrDefault(index - 1);
                if (byName != null)
                {
                    current = byName;
                    continue;
                }

                var byAbsoluteIndex = current.Elements().ElementAtOrDefault(index - 1);
                if (byAbsoluteIndex != null && string.Equals(byAbsoluteIndex.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
                {
                    current = byAbsoluteIndex;
                    continue;
                }

                current = null;
                if (current == null) return null;
            }

            return current;
        }

        private static string ResolveCanonicalAttributeName(XElement element, string requested)
        {
            var knownAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "caption", "Caption" },
                { "text", "Caption" },
                { "class", "Class" },
                { "visible", "Visible" },
                { "enabled", "Enabled" },
                { "readonly", "ReadOnly" },
                { "x", "Left" },
                { "left", "Left" },
                { "y", "Top" },
                { "top", "Top" }
            };

            if (knownAliases.TryGetValue(requested ?? string.Empty, out string alias))
            {
                requested = alias;
            }

            var existing = element.Attributes()
                .FirstOrDefault(a => string.Equals(a.Name.LocalName, requested, StringComparison.OrdinalIgnoreCase));

            return existing != null ? existing.Name.LocalName : (requested ?? string.Empty);
        }

        private static string Attr(XElement element, string name)
        {
            var attr = element.Attribute(name);
            return attr != null ? attr.Value : null;
        }

        private static string NormalizeTextPreview(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string compact = value.Trim().Replace("\r", " ").Replace("\n", " ");
            while (compact.Contains("  "))
            {
                compact = compact.Replace("  ", " ");
            }

            if (compact.Length > 160) compact = compact.Substring(0, 160);
            return compact;
        }

        private static bool IsTextPropertyName(string propertyName)
        {
            return string.Equals(propertyName, "text", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(propertyName, "innertext", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(propertyName, "nodevalue", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(propertyName, "value", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPersistedValueMatch(string propertyName, string expected, string actual)
        {
            string normalizedExpected = expected ?? string.Empty;
            string normalizedActual = actual ?? string.Empty;

            if (string.Equals(normalizedExpected, normalizedActual, StringComparison.Ordinal))
            {
                return true;
            }

            if ((string.Equals(propertyName, "Left", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(propertyName, "Top", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(propertyName, "Width", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(propertyName, "Height", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(propertyName, "BorderWidth", StringComparison.OrdinalIgnoreCase)) &&
                int.TryParse(normalizedExpected, out int expectedInt) &&
                int.TryParse(normalizedActual, out int actualInt) &&
                expectedInt == actualInt)
            {
                return true;
            }

            // The report SDK often serializes colors as nested "Color [ ... ]" descriptors.
            if (string.Equals(propertyName, "ForeColor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(propertyName, "BackColor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(propertyName, "BorderColor", StringComparison.OrdinalIgnoreCase))
            {
                string expectedLeaf = ExtractColorLeafToken(normalizedExpected);
                string actualLeaf = ExtractColorLeafToken(normalizedActual);
                if (!string.IsNullOrWhiteSpace(expectedLeaf) &&
                    !string.IsNullOrWhiteSpace(actualLeaf) &&
                    string.Equals(expectedLeaf, actualLeaf, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (TryParseColorToken(normalizedExpected, out var expectedColor) &&
                    TryParseColorToken(normalizedActual, out var actualColor))
                {
                    if (expectedColor.ToArgb() == actualColor.ToArgb())
                    {
                        return true;
                    }
                }

                if (normalizedActual.IndexOf(normalizedExpected, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string ExtractColorLeafToken(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            string token = raw.Trim();
            if (token.StartsWith("'", StringComparison.Ordinal) &&
                token.EndsWith("'", StringComparison.Ordinal) &&
                token.Length > 1)
            {
                token = token.Substring(1, token.Length - 2).Trim();
            }

            var matches = Regex.Matches(token, @"\[(?<name>[^\[\]]+)\]");
            if (matches.Count > 0)
            {
                for (int i = matches.Count - 1; i >= 0; i--)
                {
                    string candidate = matches[i].Groups["name"].Value.Trim();
                    if (!string.Equals(candidate, "Color", StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }

            return token;
        }

        private static bool TryParseColorToken(string raw, out System.Drawing.Color color)
        {
            color = System.Drawing.Color.Empty;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string token = ExtractColorLeafToken(raw);
            if (string.IsNullOrWhiteSpace(token)) return false;

            if (string.Equals(token, "Transparent", StringComparison.OrdinalIgnoreCase))
            {
                color = System.Drawing.Color.Transparent;
                return true;
            }

            var rgbMatch = Regex.Match(token, @"^\s*(\d{1,3})\s*;\s*(\d{1,3})\s*;\s*(\d{1,3})\s*\|?\s*$");
            if (rgbMatch.Success &&
                int.TryParse(rgbMatch.Groups[1].Value, out int r) &&
                int.TryParse(rgbMatch.Groups[2].Value, out int g) &&
                int.TryParse(rgbMatch.Groups[3].Value, out int b))
            {
                r = Math.Max(0, Math.Min(255, r));
                g = Math.Max(0, Math.Min(255, g));
                b = Math.Max(0, Math.Min(255, b));
                color = System.Drawing.Color.FromArgb(r, g, b);
                return true;
            }

            var named = System.Drawing.Color.FromName(token);
            if (named.IsKnownColor || named.IsNamedColor || named.IsSystemColor)
            {
                color = named;
                return true;
            }

            return false;
        }

        private static LayoutContextResult LoadVisualContext(KBObject obj, string target, VisualSurface preferredSurface)
        {
            if (preferredSurface == VisualSurface.Any || preferredSurface == VisualSurface.Report)
            {
                var reportPart = PartAccessor.GetPart(obj, "Layout");
                if (reportPart != null)
                {
                    if (ReportLayoutHelper.IsReportPart(reportPart) != null)
                    {
                        string xml = ReportLayoutHelper.ReadLayout(reportPart);
                        var parsed = ParseVisualXml(xml, target, "Procedure Layout XML not found", "The Procedure does not expose a valid ReportPart layout.");
                        if (parsed.Document != null)
                        {
                            return LayoutContextResult.FromReport(reportPart, parsed.Document);
                        }

                        if (preferredSurface == VisualSurface.Report)
                        {
                            return LayoutContextResult.FromError(parsed.Error);
                        }
                    }
                    else
                    {
                        Logger.Debug($"Part 'Layout' found for {obj.Name} but rejected by ReportLayoutHelper (Type: {reportPart.TypeDescriptor?.Name ?? "Unknown"}, GUID: {reportPart.Type})");
                    }
                }
            }

            if (preferredSurface == VisualSurface.Any || preferredSurface == VisualSurface.WebForm)
            {
                var webFormPart = WebFormXmlHelper.GetWebFormPart(obj);
                if (webFormPart != null)
                {
                    string xml = WebFormXmlHelper.ReadEditableXml(obj);
                    var parsed = ParseVisualXml(xml, target, "Layout/WebForm XML not found", "The object does not expose editable visual XML.");
                    if (parsed.Document != null)
                    {
                        return LayoutContextResult.FromWebForm(webFormPart, parsed.Document);
                    }

                    if (preferredSurface == VisualSurface.WebForm)
                    {
                        return LayoutContextResult.FromError(parsed.Error);
                    }
                }
            }

            if (preferredSurface == VisualSurface.Any || preferredSurface == VisualSurface.LayoutSource)
            {
                var layoutResult = TryLoadXmlFromPart(obj, target, "Layout");
                if (layoutResult != null)
                {
                    return layoutResult;
                }

                var patternVirtualResult = TryLoadXmlFromPart(obj, target, "PatternVirtual");
                if (patternVirtualResult != null)
                {
                    return patternVirtualResult;
                }

                if (preferredSurface == VisualSurface.LayoutSource)
                {
                    return LayoutContextResult.FromError(
                        Models.McpResponse.Error("Layout part not found", target, "Layout", "The object does not expose a textual Layout part for visual editing."));
                }
            }

            return LayoutContextResult.FromError(
                Models.McpResponse.Error("Visual part not found", target, "Layout", "The object does not expose a supported visual part (WebForm or Layout source XML)."));
        }

        private static LayoutContextResult TryLoadXmlFromPart(KBObject obj, string target, string partName)
        {
            var part = PartAccessor.GetPart(obj, partName);
            if (part == null) return null;

            if (part is ISource sourcePart)
            {
                string xml = sourcePart.Source;
                var parsed = ParseVisualXml(xml, target, $"{partName} source is empty", $"The object {partName} source is empty or not available.");
                if (parsed.Document != null)
                {
                    return LayoutContextResult.FromLayoutSource(partName, sourcePart, parsed.Document);
                }

                return LayoutContextResult.FromError(parsed.Error);
            }

            var reflectiveXml = TryExtractXmlFromPartMembers(part, target, partName);
            if (reflectiveXml != null)
            {
                return reflectiveXml;
            }

            string serializedXml;
            try
            {
                serializedXml = part.SerializeToXml();
            }
            catch
            {
                return null;
            }

            var parsedXml = ParseVisualXml(serializedXml, target, $"{partName} XML is empty", $"The object {partName} XML is empty or not available.");
            if (parsedXml.Document == null)
            {
                return LayoutContextResult.FromError(parsedXml.Error);
            }

            return LayoutContextResult.FromPartXml(partName, part, parsedXml.Document);
        }

        private static LayoutContextResult TryExtractXmlFromPartMembers(KBObjectPart part, string target, string partName)
        {
            var candidates = CollectXmlCandidates(part, includeNonPublic: true, includeNested: true);

            var best = candidates
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.Document.Descendants().Count())
                .FirstOrDefault();

            if (best == null)
            {
                return null;
            }

            string memberName = best.Property != null ? best.Property.Name : best.GetterMethod?.Name;
            bool writable = best.Property != null && best.Property.CanWrite;
            return LayoutContextResult.FromPartMemberXml(partName, part, best.Document, memberName, best.SourcePath, writable);
        }

        private static List<MemberXmlCandidate> CollectXmlCandidates(KBObjectPart part, bool includeNonPublic, bool includeNested)
        {
            var candidates = new List<MemberXmlCandidate>();
            var visited = new HashSet<object>(ReferenceObjectComparer.Instance);
            CollectXmlCandidatesFromObject(
                part,
                part.GetType().Name,
                depth: 0,
                maxDepth: includeNested ? 2 : 0,
                includeNonPublic: includeNonPublic,
                candidates: candidates,
                visited: visited);

            return candidates;
        }

        private static void CollectXmlCandidatesFromObject(
            object instance,
            string sourcePath,
            int depth,
            int maxDepth,
            bool includeNonPublic,
            List<MemberXmlCandidate> candidates,
            HashSet<object> visited)
        {
            if (instance == null || depth > maxDepth) return;
            if (!visited.Add(instance)) return;

            var flags = BindingFlags.Public | BindingFlags.Instance;
            if (includeNonPublic) flags |= BindingFlags.NonPublic;

            var type = instance.GetType();

            foreach (var prop in type.GetProperties(flags))
            {
                if (prop.GetIndexParameters().Length > 0 || !prop.CanRead) continue;

                bool accessorPublic = prop.GetMethod != null && prop.GetMethod.IsPublic;

                if (prop.PropertyType == typeof(string) && LooksLikeXmlCarrierName(prop.Name))
                {
                    string value;
                    try { value = prop.GetValue(instance) as string; } catch { value = null; }

                    var parsed = TryParseCandidateXml(value);
                    if (parsed != null)
                    {
                        string candidatePath = sourcePath + "." + prop.Name;
                        candidates.Add(new MemberXmlCandidate
                        {
                            Xml = value,
                            Document = parsed,
                            Score = ScoreVisualXml(parsed) + ScoreSourcePath(candidatePath),
                            Property = prop,
                            MemberName = prop.Name,
                            SourcePath = candidatePath,
                            Depth = depth,
                            MemberKind = accessorPublic ? "property" : "property_nonpublic",
                            MemberWritable = prop.SetMethod != null && (prop.SetMethod.IsPublic || includeNonPublic)
                        });
                    }
                }

                if (depth >= maxDepth) continue;
                if (!ShouldTraverseMember(prop.Name, prop.PropertyType)) continue;

                object nested;
                try { nested = prop.GetValue(instance); } catch { nested = null; }
                if (nested == null) continue;

                CollectXmlCandidatesFromObject(
                    nested,
                    sourcePath + "." + prop.Name,
                    depth + 1,
                    maxDepth,
                    includeNonPublic,
                    candidates,
                    visited);
            }

            foreach (var method in type.GetMethods(flags))
            {
                if (method.GetParameters().Length != 0) continue;
                if (method.IsSpecialName) continue;
                if (method.ReturnType != typeof(string)) continue;
                if (!LooksLikeXmlCarrierName(method.Name)) continue;
                if (string.Equals(method.Name, "ToString", StringComparison.Ordinal)) continue;

                string value;
                try { value = method.Invoke(instance, null) as string; } catch { value = null; }

                var parsed = TryParseCandidateXml(value);
                if (parsed == null) continue;

                string candidatePath = sourcePath + "." + method.Name + "()";
                candidates.Add(new MemberXmlCandidate
                {
                    Xml = value,
                    Document = parsed,
                    Score = ScoreVisualXml(parsed) + ScoreSourcePath(candidatePath),
                    GetterMethod = method,
                    MemberName = method.Name,
                    SourcePath = candidatePath,
                    Depth = depth,
                    MemberKind = method.IsPublic ? "method" : "method_nonpublic",
                    MemberWritable = false
                });
            }
        }

        private static bool LooksLikeXmlCarrierName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("xml") || n.Contains("layout") || n.Contains("source") || n.Contains("metadata") || n.Contains("content") || n.Contains("form") || n.Contains("control");
        }

        private static bool ShouldTraverseMember(string memberName, Type memberType)
        {
            if (memberType == null) return false;
            if (memberType == typeof(string)) return false;
            if (memberType.IsPrimitive || memberType.IsEnum) return false;
            if (typeof(IEnumerable).IsAssignableFrom(memberType) && memberType != typeof(byte[])) return false;

            string typeName = memberType.FullName ?? memberType.Name ?? string.Empty;
            string lowerType = typeName.ToLowerInvariant();
            string lowerName = (memberName ?? string.Empty).ToLowerInvariant();

            bool strongHint =
                lowerName.Contains("layout") ||
                lowerName.Contains("form") ||
                lowerName.Contains("xml") ||
                lowerName.Contains("control") ||
                lowerName.Contains("meta") ||
                lowerType.Contains("layout") ||
                lowerType.Contains("form") ||
                lowerType.Contains("metadata") ||
                lowerType.Contains("control") ||
                lowerType.Contains("artech.genexus");

            return strongHint;
        }

        private static int ScoreSourcePath(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return 0;

            string p = sourcePath.ToLowerInvariant();
            int score = 0;
            if (p.Contains("layout")) score += 20;
            if (p.Contains("form")) score += 15;
            if (p.Contains("control")) score += 20;
            if (p.Contains("metadata")) score += 8;
            if (p.Contains("xml")) score += 12;
            return score;
        }

        private static bool TryPersistViaSourcePath(object root, string sourcePath, string normalizedXml)
        {
            if (root == null || string.IsNullOrWhiteSpace(sourcePath))
            {
                return false;
            }

            var target = ResolveSourcePathOwner(root, sourcePath);
            if (target == null)
            {
                return false;
            }

            if (TryInvokeXmlSetterMethods(target, normalizedXml))
            {
                return true;
            }

            return TrySetXmlLikeProperty(target, normalizedXml);
        }

        private static object ResolveSourcePathOwner(object root, string sourcePath)
        {
            var segments = sourcePath.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return null;

            int start = 0;
            if (string.Equals(segments[0], root.GetType().Name, StringComparison.OrdinalIgnoreCase))
            {
                start = 1;
            }

            object current = root;
            for (int i = start; i < segments.Length - 1; i++)
            {
                string segment = segments[i];
                if (segment.EndsWith("()", StringComparison.Ordinal))
                {
                    break;
                }

                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var type = current.GetType();

                var prop = type.GetProperty(segment, flags);
                if (prop != null && prop.CanRead)
                {
                    try
                    {
                        current = prop.GetValue(current);
                        if (current == null) return null;
                        continue;
                    }
                    catch
                    {
                        return null;
                    }
                }

                var field = type.GetField(segment, flags);
                if (field != null)
                {
                    try
                    {
                        current = field.GetValue(current);
                        if (current == null) return null;
                        continue;
                    }
                    catch
                    {
                        return null;
                    }
                }

                return null;
            }

            return current;
        }

        private static bool TryInvokeXmlSetterMethods(object target, string normalizedXml)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = target.GetType();

            string[] prioritized = { "DeserializeFromXml", "LoadFromXml", "ApplyXml", "SetXml", "SetLayoutXml", "SetSource" };
            foreach (var methodName in prioritized)
            {
                var method = type
                    .GetMethods(flags)
                    .FirstOrDefault(m =>
                        string.Equals(m.Name, methodName, StringComparison.Ordinal) &&
                        m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType == typeof(string));
                if (method == null) continue;

                try
                {
                    method.Invoke(target, new object[] { normalizedXml });
                    return true;
                }
                catch
                {
                }
            }

            foreach (var method in type.GetMethods(flags))
            {
                if (method.IsSpecialName) continue;
                var parameters = method.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(string)) continue;

                string name = method.Name.ToLowerInvariant();
                bool looksLikeSetter =
                    (name.Contains("set") || name.Contains("load") || name.Contains("apply") || name.Contains("deserialize")) &&
                    (name.Contains("xml") || name.Contains("layout") || name.Contains("source"));
                if (!looksLikeSetter) continue;

                try
                {
                    method.Invoke(target, new object[] { normalizedXml });
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TrySetXmlLikeProperty(object target, string normalizedXml)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = target.GetType();

            foreach (var prop in type.GetProperties(flags))
            {
                if (!prop.CanWrite || prop.PropertyType != typeof(string)) continue;

                string name = prop.Name.ToLowerInvariant();
                bool looksLikeXml =
                    name.Contains("xml") || name.Contains("layout") || name.Contains("source") || name.Contains("metadata") || name.Contains("content");
                if (!looksLikeXml) continue;

                try
                {
                    prop.SetValue(target, normalizedXml);
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        private static XDocument TryParseCandidateXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            string trimmed = xml.TrimStart();
            if (!trimmed.StartsWith("<", StringComparison.Ordinal)) return null;

            try
            {
                return XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            }
            catch
            {
                return null;
            }
        }

        private static int ScoreVisualXml(XDocument doc)
        {
            if (doc?.Root == null) return 0;

            int score = 0;
            string root = doc.Root.Name.LocalName;
            int totalNodes = doc.Descendants().Count();
            score += totalNodes;

            if (!string.Equals(root, "Properties", StringComparison.OrdinalIgnoreCase))
            {
                score += 50;
            }

            int controlAttrs = doc.Descendants().Count(e => e.Attribute("ControlName") != null);
            int captionAttrs = doc.Descendants().Count(e => e.Attribute("Caption") != null);
            int internalNameAttrs = doc.Descendants().Count(e => e.Attribute("InternalName") != null);
            score += controlAttrs * 200;
            score += captionAttrs * 40;
            score += internalNameAttrs * 40;

            return score;
        }

        private static ParseResult ParseVisualXml(string xml, string target, string emptyMessage, string emptyDetails)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                return ParseResult.FromError(Models.McpResponse.Error(emptyMessage, target, "Layout", emptyDetails));
            }

            try
            {
                var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                if (doc.Root == null)
                {
                    return ParseResult.FromError(Models.McpResponse.Error("Invalid visual XML", target, "Layout", "The visual XML root element is missing."));
                }

                return ParseResult.FromDocument(doc);
            }
            catch (Exception ex)
            {
                return ParseResult.FromError(Models.McpResponse.Error("Invalid visual XML", target, "Layout", ex.Message));
            }
        }

        private string PersistVisualXml(KBObject obj, LayoutContextResult context, string target, string normalizedXml)
        {
            var kb = _objectService.GetKbService().GetKB();
            if (kb == null)
            {
                return Models.McpResponse.Error("KB not opened", target, "Layout", "Open a Knowledge Base before writing visual metadata.");
            }

            using (var transaction = kb.BeginTransaction())
            {
                try
                {
                    if (context.Surface == VisualSurface.Report)
                    {
                        if (!TryNormalizeReportPrintCommandsInSourceInMemory(obj, normalizedXml, out string normalizeError))
                        {
                            return Models.McpResponse.Error("Layout mutation failed", target, "Layout", normalizeError);
                        }

                        if (!TryFlushSourceForLayoutMutation(obj, out string flushSourceError))
                        {
                            return Models.McpResponse.Error("Layout mutation failed", target, "Layout", flushSourceError);
                        }

                        if (!ReportLayoutHelper.WriteLayout(context.VisualPart, normalizedXml))
                        {
                            return Models.McpResponse.Error("Layout mutation failed", target, "Layout", "ReportLayoutHelper failed to write XML to the ReportPart.");
                        }
                    }
                    else if (context.Surface == VisualSurface.WebForm)
                    {
                        WebFormXmlHelper.ApplyEditableXml(context.WebFormPart, normalizedXml);
                        try { context.WebFormPart.Save(); } catch { }
                    }
                    else if (context.Surface == VisualSurface.LayoutSource)
                    {
                        context.SourcePart.Source = normalizedXml;
                        try
                        {
                            var saveMethod = context.SourcePart.GetType().GetMethod("Save", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            saveMethod?.Invoke(context.SourcePart, null);
                        }
                        catch { }
                    }
                    else if (context.Surface == VisualSurface.PartXml)
                    {
                        context.VisualPart.DeserializeFromXml(normalizedXml);
                        try
                        {
                            var saveMethod = context.VisualPart.GetType().GetMethod("Save", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            saveMethod?.Invoke(context.VisualPart, null);
                        }
                        catch { }
                    }
                    else if (context.Surface == VisualSurface.PartMemberXml)
                    {
                        bool persisted = false;

                        if (!string.IsNullOrWhiteSpace(context.MemberName) && context.MemberWritable)
                        {
                            var prop = context.VisualPart.GetType().GetProperty(context.MemberName, BindingFlags.Public | BindingFlags.Instance);
                            if (prop != null && prop.CanWrite && prop.PropertyType == typeof(string))
                            {
                                prop.SetValue(context.VisualPart, normalizedXml);
                                persisted = true;
                            }
                        }

                        if (!persisted)
                        {
                            persisted = TryPersistViaSourcePath(context.VisualPart, context.MemberSourcePath, normalizedXml);
                        }

                        if (!persisted)
                        {
                            try
                            {
                                context.VisualPart.DeserializeFromXml(normalizedXml);
                                persisted = true;
                            }
                            catch (Exception deserializeEx)
                            {
                                return Models.McpResponse.Error(
                                    "Layout mutation failed",
                                    target,
                                    "Layout",
                                    "Resolved visual member is not writable and DeserializeFromXml fallback failed: " + deserializeEx.Message);
                            }
                        }

                        try
                        {
                            var saveMethod = context.VisualPart.GetType().GetMethod("Save", BindingFlags.Public | BindingFlags.Instance);
                            saveMethod?.Invoke(context.VisualPart, null);
                        }
                        catch { }
                    }
                    else
                    {
                        return Models.McpResponse.Error("Unsupported visual surface", target, "Layout", "The selected visual surface cannot be persisted.");
                    }

                    obj.EnsureSave(true);
                    transaction.Commit();
                    _objectService.MarkReadCacheDirty(obj, context.PartName ?? "Layout");
                    return null;
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    return Models.McpResponse.Error("Layout mutation failed", target, "Layout", ex.Message);
                }
            }
        }

        private bool TryRenamePrintCommandInSource(KBObject obj, string currentName, string newName, out string error)
        {
            error = null;
            if (obj == null)
            {
                error = "Object was not available for source synchronization.";
                return false;
            }

            string sourceJson = _objectService.ReadObjectSource(obj.Name, "Source", null, null, "mcp", false, obj.TypeDescriptor?.Name);
            JObject sourcePayload;
            try
            {
                sourcePayload = JObject.Parse(sourceJson);
            }
            catch
            {
                error = "Could not parse Source payload while renaming print block.";
                return false;
            }

            string source = sourcePayload["source"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(source))
            {
                error = "Procedure Source is empty; unable to rename print command.";
                return false;
            }

            string pattern = @"(?im)(^|\s)print\s+" + Regex.Escape(currentName) + @"(\s|$)";
            int replacements = 0;
            string updated = Regex.Replace(source, pattern, m =>
            {
                replacements++;
                string prefix = m.Groups[1].Value;
                string suffix = m.Groups[2].Value;
                return prefix + "print " + newName + suffix;
            });

            if (replacements == 0)
            {
                error = "No matching print command was found in Source for '" + currentName + "'.";
                return false;
            }

            return TryPersistSourceText(obj, updated, out error);
        }

        private bool TryRenamePrintCommandInSourceInMemory(KBObject obj, string currentName, string newName, out string error)
        {
            error = null;
            if (obj == null)
            {
                error = "Object was not available for source synchronization.";
                return false;
            }

            var sourcePart = PartAccessor.GetPart(obj, "Source") as ISource;
            if (sourcePart == null)
            {
                error = "Procedure Source part was not available for in-memory synchronization.";
                return false;
            }

            string source = sourcePart.Source ?? string.Empty;
            if (string.IsNullOrWhiteSpace(source))
            {
                error = "Procedure Source is empty; unable to rename print command.";
                return false;
            }

            string pattern = @"(?im)(^|\s)print\s+" + Regex.Escape(currentName) + @"(\s|$)";
            int replacements = 0;
            string updated = Regex.Replace(source, pattern, m =>
            {
                replacements++;
                string prefix = m.Groups[1].Value;
                string suffix = m.Groups[2].Value;
                return prefix + "print " + newName + suffix;
            });

            if (replacements == 0)
            {
                bool alreadyRenamed = Regex.IsMatch(
                    source,
                    @"(?im)(^|\s)print\s+" + Regex.Escape(newName) + @"(\s|$)");
                if (alreadyRenamed)
                {
                    return true;
                }

                error = "No matching print command was found in Source for '" + currentName + "'.";
                return false;
            }

            sourcePart.Source = updated;
            return true;
        }

        private bool TryInsertPrintCommandInSource(KBObject obj, string printBlockName, out string error)
        {
            error = null;
            if (obj == null)
            {
                error = "Object was not available for source synchronization.";
                return false;
            }

            string sourceJson = _objectService.ReadObjectSource(obj.Name, "Source", null, null, "mcp", false, obj.TypeDescriptor?.Name);
            JObject sourcePayload;
            try
            {
                sourcePayload = JObject.Parse(sourceJson);
            }
            catch
            {
                error = "Could not parse Source payload while inserting print command.";
                return false;
            }

            string source = sourcePayload["source"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(source))
            {
                error = "Procedure Source is empty; unable to insert print command.";
                return false;
            }

            if (Regex.IsMatch(source, @"(?im)(^|\s)print\s+" + Regex.Escape(printBlockName) + @"(\s|$)"))
            {
                // Source already synchronized.
                return true;
            }

            string lineEnding = source.Contains("\r\n") ? "\r\n" : "\n";
            string insertion = "print " + printBlockName;
            string updated;

            var anchor = Regex.Match(source, @"(?im)^[ \t]*print[ \t]+printblock2[ \t]*$");
            if (anchor.Success)
            {
                updated = source.Insert(anchor.Index, insertion + lineEnding);
            }
            else
            {
                var footerAnchor = Regex.Match(source, @"(?im)^[ \t]*Footer[ \t]*$");
                if (footerAnchor.Success)
                {
                    updated = source.Insert(footerAnchor.Index, insertion + lineEnding);
                }
                else
                {
                    if (!source.EndsWith(lineEnding, StringComparison.Ordinal))
                    {
                        source += lineEnding;
                    }

                    updated = source + insertion + lineEnding;
                }
            }

            return TryPersistSourceText(obj, updated, out error);
        }

        private bool TryInsertPrintCommandInSourceInMemory(KBObject obj, string printBlockName, out string error)
        {
            error = null;
            if (obj == null)
            {
                error = "Object was not available for source synchronization.";
                return false;
            }

            var sourcePart = PartAccessor.GetPart(obj, "Source") as ISource;
            if (sourcePart == null)
            {
                error = "Procedure Source part was not available for in-memory synchronization.";
                return false;
            }

            string source = sourcePart.Source ?? string.Empty;
            if (string.IsNullOrWhiteSpace(source))
            {
                error = "Procedure Source is empty; unable to insert print command.";
                return false;
            }

            if (Regex.IsMatch(source, @"(?im)(^|\s)print\s+" + Regex.Escape(printBlockName) + @"(\s|$)"))
            {
                return true;
            }

            string lineEnding = source.Contains("\r\n") ? "\r\n" : "\n";
            string insertion = "print " + printBlockName;
            string updated;

            var anchor = Regex.Match(source, @"(?im)^[ \t]*print[ \t]+printblock2[ \t]*$");
            if (anchor.Success)
            {
                updated = source.Insert(anchor.Index, insertion + lineEnding);
            }
            else
            {
                var footerAnchor = Regex.Match(source, @"(?im)^[ \t]*Footer[ \t]*$");
                if (footerAnchor.Success)
                {
                    updated = source.Insert(footerAnchor.Index, insertion + lineEnding);
                }
                else
                {
                    if (!source.EndsWith(lineEnding, StringComparison.Ordinal))
                    {
                        source += lineEnding;
                    }

                    updated = source + insertion + lineEnding;
                }
            }

            sourcePart.Source = updated;
            return true;
        }

        private static string GetProcedureSourceSnapshot(KBObject obj)
        {
            var sourcePart = PartAccessor.GetPart(obj, "Source") as ISource;
            return sourcePart?.Source;
        }

        private bool TryRestoreProcedureSource(KBObject obj, string sourceSnapshot)
        {
            if (obj == null || sourceSnapshot == null)
            {
                return false;
            }

            var sourcePart = PartAccessor.GetPart(obj, "Source") as ISource;
            if (sourcePart == null)
            {
                return false;
            }

            try
            {
                sourcePart.Source = sourceSnapshot;
                obj.EnsureSave(false);
                _objectService.MarkReadCacheDirty(obj, "Source");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("TryRestoreProcedureSource failed: " + ex.Message);
                return false;
            }
        }

        private bool TryFlushSourceForLayoutMutation(KBObject obj, out string error)
        {
            error = null;
            if (obj == null)
            {
                error = "Object was not available for source flush.";
                return false;
            }

            var sourcePart = PartAccessor.GetPart(obj, "Source") as ISource;
            if (sourcePart == null)
            {
                return true;
            }

            try
            {
                var saveMethod = sourcePart.GetType().GetMethod("Save", BindingFlags.Public | BindingFlags.Instance);
                if (saveMethod != null)
                {
                    try
                    {
                        saveMethod.Invoke(sourcePart, null);
                    }
                    catch (TargetInvocationException tiex)
                    {
                        string inner = tiex.InnerException?.Message;
                        Logger.Warn("TryFlushSourceForLayoutMutation Source.Save failed: " + (inner ?? tiex.Message));
                    }
                    catch (Exception saveEx)
                    {
                        Logger.Warn("TryFlushSourceForLayoutMutation Source.Save failed: " + saveEx.Message);
                    }
                }

                obj.EnsureSave(false);
                _objectService.MarkReadCacheDirty(obj, "Source");
                return true;
            }
            catch (Exception ex)
            {
                error = "Failed to flush Procedure Source before report mutation: " + ex.Message;
                return false;
            }
        }

        private static bool TrySaveVisualPart(KBObjectPart visualPart, out string error)
        {
            error = null;
            if (visualPart == null)
            {
                error = "Visual part was not available for save.";
                return false;
            }

            try
            {
                visualPart.Save();
                return true;
            }
            catch (Exception ex)
            {
                error = "Visual part save failed after staging layout mutation: " + ex.Message;
                return false;
            }
        }

        private bool TryNormalizeReportPrintCommandsInSourceInMemory(KBObject obj, string reportXml, out string error)
        {
            error = null;
            if (obj == null)
            {
                error = "Object was not available for source normalization.";
                return false;
            }

            var sourcePart = PartAccessor.GetPart(obj, "Source") as ISource;
            if (sourcePart == null)
            {
                // Some report procedures may not expose editable Source in this context.
                return true;
            }

            string source = sourcePart.Source ?? string.Empty;
            if (string.IsNullOrWhiteSpace(source))
            {
                return true;
            }

            XDocument doc;
            try
            {
                doc = XDocument.Parse(reportXml);
            }
            catch
            {
                // If report xml cannot be parsed here, keep source untouched.
                return true;
            }

            var canonicalByLower = doc
                .Descendants("PrintBlock")
                .Select(pb => Attr(pb, "Name") ?? Attr(pb, "ControlName"))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(n => n.ToLowerInvariant(), n => n, StringComparer.OrdinalIgnoreCase);

            if (canonicalByLower.Count == 0)
            {
                return true;
            }

            bool changed = false;
            string normalized = Regex.Replace(
                source,
                @"(?im)^(?<indent>\s*)print\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?<tail>(//.*)?)$",
                m =>
                {
                    string original = m.Groups["name"].Value;
                    string lower = original.ToLowerInvariant();
                    if (!canonicalByLower.TryGetValue(lower, out string canonical))
                    {
                        if (lower.EndsWith("_mcp", StringComparison.OrdinalIgnoreCase))
                        {
                            string baseName = original.Substring(0, original.Length - 4);
                            canonicalByLower.TryGetValue(baseName.ToLowerInvariant(), out canonical);
                        }
                    }

                    if (string.IsNullOrWhiteSpace(canonical))
                    {
                        if (lower.StartsWith("printblock", StringComparison.OrdinalIgnoreCase))
                        {
                            changed = true;
                            return string.Empty;
                        }

                        return m.Value;
                    }

                    if (string.Equals(original, canonical, StringComparison.Ordinal))
                    {
                        return m.Value;
                    }

                    changed = true;
                    return $"{m.Groups["indent"].Value}print {canonical}{m.Groups["tail"].Value}";
                });

            if (changed)
            {
                sourcePart.Source = normalized;
            }

            return true;
        }

        private bool TryPersistSourceText(KBObject obj, string sourceText, out string error)
        {
            error = null;
            string tempPath = null;
            try
            {
                tempPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "gxmcp-layout-source-" + Guid.NewGuid().ToString("N") + ".txt");
                System.IO.File.WriteAllText(tempPath, sourceText ?? string.Empty);

                string importResult = _objectService.ImportObjectFromText(
                    obj.Name,
                    tempPath,
                    "Source",
                    obj.TypeDescriptor?.Name);

                JObject parsed;
                try
                {
                    parsed = JObject.Parse(importResult);
                }
                catch
                {
                    error = "Source import returned an invalid payload.";
                    return false;
                }

                string status = parsed["status"]?.ToString();
                if (!string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase))
                {
                    error = parsed["error"]?.ToString() ?? parsed["details"]?.ToString() ?? "Source import failed.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempPath))
                {
                    try { System.IO.File.Delete(tempPath); } catch { }
                }
            }
        }

        private sealed class FindCriteria
        {
            public string PropertyName { get; set; }
            public string Query { get; set; }
        }

        private sealed class ParseResult
        {
            public XDocument Document { get; private set; }
            public string Error { get; private set; }

            public static ParseResult FromDocument(XDocument document) => new ParseResult { Document = document };
            public static ParseResult FromError(string error) => new ParseResult { Error = error };
        }

        private sealed class LayoutContextResult
        {
            public VisualSurface Surface { get; private set; }
            public dynamic WebFormPart { get; private set; }
            public ISource SourcePart { get; private set; }
            public KBObjectPart VisualPart { get; private set; }
            public string PartName { get; private set; }
            public string MemberName { get; private set; }
            public string MemberSourcePath { get; private set; }
            public bool MemberWritable { get; private set; }
            public XDocument Document { get; private set; }
            public string Error { get; private set; }

            public static LayoutContextResult FromError(string error) => new LayoutContextResult { Error = error };

            public static LayoutContextResult FromWebForm(dynamic webFormPart, XDocument document)
            {
                return new LayoutContextResult
                {
                    Surface = VisualSurface.WebForm,
                    WebFormPart = webFormPart,
                    Document = document
                };
            }

            public static LayoutContextResult FromLayoutSource(ISource sourcePart, XDocument document)
            {
                return new LayoutContextResult
                {
                    Surface = VisualSurface.LayoutSource,
                    PartName = "Layout",
                    SourcePart = sourcePart,
                    Document = document
                };
            }

            public static LayoutContextResult FromLayoutSource(string partName, ISource sourcePart, XDocument document)
            {
                return new LayoutContextResult
                {
                    Surface = VisualSurface.LayoutSource,
                    PartName = partName,
                    SourcePart = sourcePart,
                    Document = document
                };
            }

            public static LayoutContextResult FromReport(KBObjectPart reportPart, XDocument document)
            {
                return new LayoutContextResult
                {
                    Surface = VisualSurface.Report,
                    PartName = "Layout",
                    VisualPart = reportPart,
                    Document = document
                };
            }

            public static LayoutContextResult FromPartXml(string partName, KBObjectPart part, XDocument document)
            {
                return new LayoutContextResult
                {
                    Surface = VisualSurface.PartXml,
                    PartName = partName,
                    VisualPart = part,
                    Document = document
                };
            }

            public static LayoutContextResult FromPartMemberXml(string partName, KBObjectPart part, XDocument document, string memberName, string memberSourcePath, bool memberWritable)
            {
                return new LayoutContextResult
                {
                    Surface = VisualSurface.PartMemberXml,
                    PartName = partName,
                    VisualPart = part,
                    MemberName = memberName,
                    MemberSourcePath = memberSourcePath,
                    MemberWritable = memberWritable,
                    Document = document
                };
            }
        }

        private sealed class MemberXmlCandidate
        {
            public string Xml { get; set; }
            public XDocument Document { get; set; }
            public int Score { get; set; }
            public PropertyInfo Property { get; set; }
            public MethodInfo GetterMethod { get; set; }
            public string MemberName { get; set; }
            public string SourcePath { get; set; }
            public int Depth { get; set; }
            public string MemberKind { get; set; }
            public bool MemberWritable { get; set; }
        }

        private sealed class ReferenceObjectComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceObjectComparer Instance = new ReferenceObjectComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }

        private enum VisualSurface
        {
            Any,
            Report,
            WebForm,
            LayoutSource,
            PartXml,
            PartMemberXml
        }
    }
}
