using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

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
                var obj = _objectService.FindObject(target);
                if (obj == null) return "{\"error\": \"Object not found\"}";

                string code = _objectService.GetObjectSource(target);
                if (string.IsNullOrEmpty(code)) return "{\"status\": \"No source code to lint\"}";

                var issues = new JArray();

                // 1. Commit inside Loop (Critical)
                CheckCommitInsideLoop(code, issues);

                // 2. Unfiltered Loop (Critical)
                CheckUnfilteredLoop(code, issues);

                // 3. Sleep/Wait (Warning)
                CheckSleepWait(code, issues);

                // 4. Dynamic Call (Warning)
                CheckDynamicCall(code, issues);

                // 5. New without When Duplicate (Info)
                CheckNewWhenDuplicate(code, issues);

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

        private JObject CreateIssue(string code, string title, string severity, string description, string snippet)
        {
            var issue = new JObject();
            issue["code"] = code;
            issue["title"] = title;
            issue["severity"] = severity;
            issue["description"] = description;
            issue["snippet"] = snippet.Length > 200 ? snippet.Substring(0, 197).Trim() + "..." : snippet.Trim();
            return issue;
        }
    }
}
