using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using Artech.Common.Diagnostics;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Structure;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class ValidationService
    {
        private readonly KbService _kbService;
        private ObjectService _objectService;

        public ValidationService(KbService kbService)
        {
            _kbService = kbService;
        }

        public void SetObjectService(ObjectService os) { _objectService = os; }

        public string ValidateCode(string target, string partName, string code)
        {
            try
            {
                var busyMsg = _kbService.EnsureNotIndexing();
                if (busyMsg != null) return busyMsg;

                var obj = _objectService.FindObject(target);
                if (obj == null) return McpResponse.Error("Object not found for validation", target, partName, "The requested object is not available in the active Knowledge Base.");

                // 1. FAST PRE-FLIGHT CHECK (Regex based)
                var structuralErrors = CodeParser.Validate(code);
                if (structuralErrors.Count > 0)
                {
                    var errorList = new JArray();
                    foreach (var err in structuralErrors)
                    {
                        var eObj = new JObject();
                        eObj["description"] = err;
                        eObj["severity"] = "Error";
                        
                        var lineMatch = System.Text.RegularExpressions.Regex.Match(err, @"Line (\d+):");
                        eObj["line"] = lineMatch.Success && int.TryParse(lineMatch.Groups[1].Value, out int l) ? l : 1;
                        
                        errorList.Add(eObj);
                    }
                    return new JObject {
                        ["status"] = "Error",
                        ["error"] = structuralErrors[0],
                        ["errors"] = errorList,
                        ["isPreflight"] = true
                    }.ToString();
                }

                // 2. Find the exact part that is being validated.
                string normalizedPartName = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;
                KBObjectPart part = PartAccessor.GetPart(obj, normalizedPartName);

                if (part == null) return "{\"status\":\"Success\", \"message\":\"Validation not applicable for this part type.\"}";

                // 3. Capture errors using a mock transaction
                var kb = _kbService.GetKB();
                using (var transaction = kb.BeginTransaction())
                {
                    string originalSource = (part as ISource)?.Source;
                    try
                    {
                        if (part is ISource sPart) sPart.Source = code;
                        
                        string saveError = null;
                        // 3. Attempt to Save the PART (this triggers the parser)
                        try {
                            part.Save();
                        } catch (Exception saveEx) {
                            saveError = saveEx.Message;
                            
                            // Check for SDK messages even if it threw
                            var sdkMsgs = part.GetSdkMessages();
                            if (!string.IsNullOrEmpty(sdkMsgs)) saveError += " | Details: " + sdkMsgs;

                            Logger.Debug("[VALIDATION] part.Save() threw: " + saveError);
                            
                            // Check for SDK lock common in GX18
                            if (saveError == "Erro" && _kbService.IsIndexing)
                                saveError = "Generic 'Erro' during SDK Save. The Knowledge Base is currently indexing, which may be blocking part updates.";
                        }

                        // 4. Capture diagnostics from ALL parts and the Object itself
                        var issues = SdkDiagnosticsHelper.GetDiagnostics(obj);
                        
                        // Also check for part-local messages that might not be in the global list
                        string localMsgs = part.GetSdkMessages();
                        if (!string.IsNullOrEmpty(localMsgs) && !issues.Any(i => localMsgs.Contains(i["description"]?.ToString() ?? "???")))
                        {
                            var localIssue = new JObject();
                            localIssue["description"] = localMsgs;
                            localIssue["severity"] = "Error";
                            localIssue["line"] = 1;
                            localIssue["part"] = normalizedPartName;
                            issues.Add(localIssue);
                        }

                        // Filter for errors
                        var errors = new JArray(issues.Where(i => i["severity"]?.ToString() == "Error"));

                        if (errors.Count == 0 && !string.IsNullOrEmpty(saveError))
                        {
                            // Fallback: If no formal diagnostics but Save failed, create one from exception
                            var err = new JObject();
                            err["description"] = saveError;
                            err["severity"] = "Error";
                            err["line"] = 1;
                            err["part"] = normalizedPartName;
                            errors.Add(err);
                        }

                        if (errors.Count > 0)
                        {
                            return new JObject {
                                ["status"] = "Error",
                                ["error"] = errors[0]["description"]?.ToString() ?? "Syntax Error",
                                ["errors"] = errors
                            }.ToString();
                        }

                        return "{\"status\":\"Success\", \"message\":\"Syntax check passed\"}";
                    }
                    finally
                    {
                        // 5. Restore original state and ROLLBACK
                        if (part is ISource sPart && originalSource != null) sPart.Source = originalSource;
                        transaction.Rollback();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[VALIDATION] Critical Error: " + ex.Message);
                // Ensure we return a structured error that WriteService understands
                var result = new JObject();
                result["status"] = "Error";
                result["error"] = ex.Message;
                result["errors"] = new JArray(new JObject { ["description"] = ex.Message, ["severity"] = "Error", ["line"] = 1 });
                return result.ToString();
            }
        }

        public string Check(string target, string code)
        {
            return ValidateCode(target, "Source", code);
        }
    }
}
