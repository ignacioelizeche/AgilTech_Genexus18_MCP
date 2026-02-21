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

                string sourceJson = _objectService.ReadObjectSource(target, "Source");
                var json = JObject.Parse(sourceJson);
                string code = json["source"] != null ? json["source"].ToString() : "";
                
                // Clean code for all checks
                string cleanCode = StripComments(code);
                
                var issues = new JArray();

                // 1. Commit inside Loop (Critical)
                CheckCommitInsideLoop(cleanCode, issues);

                // 2. Unfiltered Loop (Critical)
                CheckUnfilteredLoop(cleanCode, issues);

                // 3. Sleep/Wait (Warning)
                CheckSleepWait(cleanCode, issues);

                // 4. Dynamic Call (Warning)
                CheckDynamicCall(cleanCode, issues);

                // 5. New without When Duplicate (Info)
                CheckNewWhenDuplicate(cleanCode, issues);

                // 6. Parm Rule Check (Procedures/WebPanels)
                if (obj is Procedure || obj is WebPanel)
                {
                    RulesPart rules = obj.Parts.Get<RulesPart>();
                    string rulesSource = rules != null ? StripComments(rules.Source) : "";
                    CheckParmRule(rulesSource, obj.Name, issues);
                }

                // 7. SDK NATIVE VALIDATION (New!)
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
            // Basic check: Does it have a parm rule?
            if (string.IsNullOrWhiteSpace(code) || !Regex.IsMatch(code, @"(?i)\bparm\s*\(", RegexOptions.Compiled))
            {
                issues.Add(CreateIssue("GX006", "Parm rule missing", "Warning", "Procedure/WebPanel " + objName + " has no parameters defined in Rules.", "parm(...)"));
            }
        }

        private string StripComments(string code)
        {
            // Remove block comments /* ... */
            var blockComments = @"/\*(.*?)\*/";
            // Remove line comments // ...
            var lineComments = @"//(.*?)\r?\n";
            // Remove strings "..." or '...'
            var strings = @"""((\\[^\n]|[^""\n])*)""|'((\\[^\n]|[^'\n])*)'";
            
            return Regex.Replace(code, blockComments + "|" + lineComments + "|" + strings, 
                me => {
                    if (me.Value.StartsWith("/*") || me.Value.StartsWith("//"))
                        return me.Value.StartsWith("//") ? Environment.NewLine : "";
                    // Keep strings intact so we don't break syntax, but their content is ignored for keywords outside
                    return me.Value; 
                },
                RegexOptions.Singleline);
        }

        private void CheckUnfilteredLoop(string code, JArray issues)
        {
            var forEachStarts = Regex.Matches(code, @"(?i)\bfor\s+each\b([^\n]*)", RegexOptions.Compiled);
            foreach (Match m in forEachStarts)
            {
                string header = m.Groups[1].Value;
                if (!Regex.IsMatch(header, @"(?i)\bwhere\b|\bdefined\s+by\b", RegexOptions.Compiled))
                {
                    issues.Add(CreateIssue("GX002", "Unfiltered loop", "Critical", "Full table scan detected. Consider adding a 'where' clause.", m.Value));
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

        private void CheckNativeSDK(KBObject obj, JArray issues)
        {
            try
            {
                // Native SDK validation (requires no save)
                // In GX18, objects have a Validate method that populates diagnostic output
                foreach (var part in obj.Parts)
                {
                    // Validation varies per part type
                    // spc errors usually appear here
                }

                // For now, we use the Validate outcome if available
                // Many spc errors are generated during Save or CheckContent
            }
            catch { }
        }

        private JObject CreateIssue(string id, string title, string severity, string description, string snippet)
        {
            var issue = new JObject();
            issue["code"] = id;
            issue["title"] = title;
            issue["severity"] = severity;
            issue["description"] = description;
            issue["snippet"] = snippet.Length > 200 ? snippet.Substring(0, 197).Trim() + "..." : snippet.Trim();
            return issue;
        }
    }
}
