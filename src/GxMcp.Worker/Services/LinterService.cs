using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;

namespace GxMcp.Worker.Services
{
    public class LinterService
    {
        private readonly ObjectService _objectService;

        public LinterService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        public string Lint(string target)
        {
            try
            {
                KBObject obj = _objectService.FindObject(target);
                if (obj == null) return "{\"error\": \"Object not found\"}";

                // Aggregate code from all parts for better analysis
                var parts = obj.Parts.Cast<KBObjectPart>().ToList();
                var sourceCode = "";
                var rulesCode = "";
                var eventsCode = "";

                foreach (var part in parts)
                {
                    if (part is ISource sourcePart)
                    {
                        var content = sourcePart.Source ?? "";
                        if (part is RulesPart) rulesCode = content;
                        else if (part.TypeDescriptor.Name == "Events") eventsCode = content;
                        else sourceCode += content + "\n";
                    }
                }

                string fullCode = rulesCode + "\n" + eventsCode + "\n" + sourceCode;
                string cleanFullCode = StripComments(fullCode);
                
                var issues = new JArray();

                // 1. Commit inside Loop (Critical)
                CheckCommitInsideLoop(cleanFullCode, issues);

                // 2. Unfiltered Loop (Critical)
                CheckUnfilteredLoop(cleanFullCode, issues);

                // 3. Sleep/Wait (Warning)
                CheckSleepWait(cleanFullCode, issues);

                // 4. Dynamic Call (Warning)
                CheckDynamicCall(cleanFullCode, issues);

                // 5. New without When Duplicate (Info)
                CheckNewWhenDuplicate(cleanFullCode, issues);

                // 6. Undeclared Variables (Critical)
                CheckVariableUsage(cleanFullCode, obj, issues, fullCode);

                // 7. Subroutine Analysis (Unused Subs)
                CheckSubroutines(cleanFullCode, issues, fullCode);

                // 8. Parm Rule Check (Procedures/WebPanels)
                if (obj is Procedure || obj is WebPanel)
                {
                    CheckParmRule(StripComments(rulesCode), obj.Name, issues);
                }

                // 9. SDK NATIVE VALIDATION
                CheckNativeSDK(obj, issues);

                var result = new JObject();
                result["target"] = target;
                result["issueCount"] = issues.Count;
                result["issues"] = issues;

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private void CheckCommitInsideLoop(string code, JArray issues)
        {
            // Improved regex for multi-line support
            var forEachBlocks = Regex.Matches(code, @"(?is)\bfor\s+each\b.*?\bendfor\b", RegexOptions.Compiled);
            foreach (Match m in forEachBlocks)
            {
                if (Regex.IsMatch(m.Value, @"(?i)\bcommit\b", RegexOptions.Compiled))
                {
                    issues.Add(CreateIssue("GX001", "Commit inside loop", "Critical", "Avoid using Commit inside a For Each loop as it breaks the LUW and cursor.", m.Value));
                }
            }
        }

        private void CheckParmRule(string code, string objName, JArray issues)
        {
            if (string.IsNullOrWhiteSpace(code) || !Regex.IsMatch(code, @"(?i)\bparm\s*\(", RegexOptions.Compiled))
            {
                issues.Add(CreateIssue("GX006", "Parm rule missing", "Warning", "Procedure/WebPanel " + objName + " has no parameters defined in Rules.", "parm(...)"));
            }
        }

        private string StripComments(string code)
        {
            var blockComments = @"/\*(.*?)\*/";
            var lineComments = @"//(.*?)\r?\n";
            var strings = @"""((\\[^\n]|[^""\n])*)""|'((\\[^\n]|[^'\n])*)'";
            
            return Regex.Replace(code, blockComments + "|" + lineComments + "|" + strings, 
                me => {
                    if (me.Value.StartsWith("/*") || me.Value.StartsWith("//"))
                        return me.Value.StartsWith("//") ? Environment.NewLine : "";
                    return me.Value; 
                },
                RegexOptions.Singleline);
        }

        private void CheckUnfilteredLoop(string code, JArray issues)
        {
            // Improved regex for capturing header accurately even with line breaks
            var forEachStarts = Regex.Matches(code, @"(?is)\bfor\s+each\b\s*(.*?)(?:\n|where|defined by|order|optimized|using)", RegexOptions.Compiled);
            foreach (Match m in forEachStarts)
            {
                string header = m.Value;
                if (!Regex.IsMatch(header, @"(?i)\bwhere\b|\bdefined\s+by\b", RegexOptions.Compiled))
                {
                    // Check if there are where clauses further down in the block
                    // We look for 'where' before 'endfor' in the same scope. 
                    // This is a simplified check.
                    int startPos = m.Index;
                    int endPos = code.ToLower().IndexOf("endfor", startPos);
                    if (endPos > startPos)
                    {
                        string block = code.Substring(startPos, endPos - startPos);
                        if (!Regex.IsMatch(block, @"(?i)\bwhere\b|\bdefined\s+by\b", RegexOptions.Compiled))
                        {
                            issues.Add(CreateIssue("GX002", "Unfiltered loop", "Critical", "Full table scan detected. Consider adding a 'where' clause.", m.Value));
                        }
                    }
                }
            }
        }

        private void CheckSleepWait(string code, JArray issues)
        {
            var matches = Regex.Matches(code, @"(?i)\b(?:sleep|wait)\s*\(\s*\d+\s*\)", RegexOptions.Compiled);
            foreach (Match m in matches)
            {
                issues.Add(CreateIssue("GX003", "Blocking call", "Warning", "Sleep/Wait calls block server threads. Use with caution in web environments.", m.Value));
            }
        }

        private void CheckDynamicCall(string code, JArray issues)
        {
            var matches = Regex.Matches(code, @"(?i)\b(?:call|udp)\s*\(\s*&\w+\s*.*?\)", RegexOptions.Compiled);
            foreach (Match m in matches)
            {
                issues.Add(CreateIssue("GX004", "Dynamic call", "Warning", "Call via variable breaks the call tree. Use literal names if possible.", m.Value));
            }
        }

        private void CheckNewWhenDuplicate(string code, JArray issues)
        {
            var newBlocks = Regex.Matches(code, @"(?is)\bnew\b.*?\bendnew\b", RegexOptions.Compiled);
            foreach (Match m in newBlocks)
            {
                if (!Regex.IsMatch(m.Value, @"(?i)\bwhen\s+duplicate\b", RegexOptions.Compiled))
                {
                    issues.Add(CreateIssue("GX005", "New without When Duplicate", "Info", "Consider adding 'when duplicate' to handle unique index collisions gracefully.", m.Value));
                }
            }
        }

        private void CheckVariableUsage(string cleanCode, KBObject obj, JArray issues, string originalCode)
        {
            var varPart = obj.Parts.Get<VariablesPart>();
            if (varPart == null) return;

            var declaredVars = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in varPart.Variables)
            {
                if (!declaredVars.ContainsKey(v.Name))
                    declaredVars.Add(v.Name, false);
            }

            // Standard GeneXus predefined variables
            var standardVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                "Pgmname", "Pgmdesc", "Mode", "Time", "Today", "UserId", "Output_file"
            };

            // Match all &identifiers
            var matches = Regex.Matches(cleanCode, @"&([a-zA-Z0-9_]+)", RegexOptions.Compiled);
            foreach (Match m in matches)
            {
                string varName = m.Groups[1].Value;
                if (declaredVars.ContainsKey(varName))
                {
                    declaredVars[varName] = true;
                }
                else if (!standardVars.Contains(varName))
                {
                    int line = GetLineNumber(originalCode, m.Index);
                    issues.Add(CreateIssue("GX007", "Undeclared variable", "Error", $"Variable '&{varName}' is used but not defined in the object.", m.Value, line));
                }
            }

            // Flag Unused Variables
            foreach (var kvp in declaredVars)
            {
                if (!kvp.Value)
                {
                    issues.Add(CreateIssue("GX008", "Unused variable", "Warning", $"Variable '&{kvp.Key}' is defined but never used in the code.", "&" + kvp.Key));
                }
            }
        }

        private void CheckSubroutines(string code, JArray issues, string originalCode)
        {
            var subDefinitions = Regex.Matches(code, @"(?is)\bsub\s+'([^']+)'(.*?)\bendsub\b", RegexOptions.Compiled);
            var subCalls = Regex.Matches(code, @"(?i)\bdo\s+'([^']+)'", RegexOptions.Compiled);

            var definedSubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in subDefinitions)
            {
                definedSubs.Add(m.Groups[1].Value);
            }

            var calledSubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in subCalls)
            {
                calledSubs.Add(m.Groups[1].Value);
            }

            foreach (var subName in definedSubs)
            {
                if (!calledSubs.Contains(subName))
                {
                    issues.Add(CreateIssue("GX009", "Unused subroutine", "Warning", $"Subroutine '{subName}' is defined but never called.", "Sub '" + subName + "'"));
                }
            }
        }

        private int GetLineNumber(string text, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < text.Length; i++)
            {
                if (text[i] == '\n') line++;
            }
            return line;
        }

        private void CheckNativeSDK(KBObject obj, JArray issues)
        {
            // Placeholder for deeper SDK integration
        }

        private JObject CreateIssue(string id, string title, string severity, string description, string snippet, int line = 0)
        {
            var issue = new JObject();
            issue["code"] = id;
            issue["title"] = title;
            issue["severity"] = severity;
            issue["description"] = description;
            issue["line"] = line;
            issue["snippet"] = snippet.Length > 200 ? snippet.Substring(0, 197).Trim() + "..." : snippet.Trim();
            return issue;
        }
    }
}
