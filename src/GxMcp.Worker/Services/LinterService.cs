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

        public string Lint(string target, string specificPart = null)
        {
            try
            {
                KBObject obj = _objectService.FindObject(target);
                if (obj == null) return "{\"error\": \"Object not found\"}";

                var issues = new JArray();
                var parts = obj.Parts.Cast<KBObjectPart>().ToList();

                Logger.Info($"Linting {obj.Name} ({obj.TypeDescriptor.Name}). SpecificPart: {specificPart}");

                foreach (var part in parts)
                {
                    string rawPartName = GetPartName(part);
                    // NORMALIZAÇÃO: Mapeia o nome interno do SDK para o nome amigável da UI
                    string uiPartName = rawPartName;
                    if (rawPartName == "Procedure") uiPartName = "Source";
                    
                    Logger.Debug($"Processing part: {rawPartName} (UI Name: {uiPartName})");

                    if (part is ISource sourcePart)
                    {
                        string content = sourcePart.Source ?? "";
                        if (string.IsNullOrEmpty(content)) continue;

                        string cleanContent = StripComments(content);

                        if (part is RulesPart)
                        {
                            CheckRulesPart(cleanContent, content, issues, "Rules");
                            if (obj is Procedure || obj is WebPanel)
                                CheckParmRule(cleanContent, obj.Name, issues, "Rules");
                        }
                        else if (rawPartName == "Events" || rawPartName == "Procedure" || rawPartName == "Source")
                        {
                            // Validação de filtro de part vindo da UI
                            if (!string.IsNullOrEmpty(specificPart) && !specificPart.Equals(uiPartName, StringComparison.OrdinalIgnoreCase))
                                continue;

                            CheckLogicPart(cleanContent, content, issues, uiPartName);
                            if (rawPartName == "Procedure" || rawPartName == "Source")
                                CheckSubroutines(cleanContent, issues, content, uiPartName);
                        }
                    }
                    else if (part is VariablesPart varPart)
                    {
                        if (string.IsNullOrEmpty(specificPart) || specificPart == "Variables")
                            CheckVariableUsageInObject(obj, issues, "Variables");
                    }
                }

                // 9. SDK NATIVE VALIDATION (Always run for the whole object)
                CheckNativeSDK(obj, issues);

                var result = new JObject();
                result["target"] = target;
                result["issueCount"] = issues.Count;
                result["issues"] = issues;

                Logger.Info($"Linting finished for {obj.Name}. Issues found: {issues.Count}");

                return result.ToString();
            }
            catch (Exception ex)
            {
                Logger.Error("Linter Error: " + ex.Message);
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string GetPartName(KBObjectPart part)
        {
            if (part is RulesPart) return "Rules";
            if (part is VariablesPart) return "Variables";
            return part.TypeDescriptor.Name;
        }

        private void CheckLogicPart(string cleanCode, string originalCode, JArray issues, string partName)
        {
            CheckCommitInsideLoop(cleanCode, issues, originalCode, partName);
            CheckUnfilteredLoop(cleanCode, issues, originalCode, partName);
            CheckSleepWait(cleanCode, issues, originalCode, partName);
            CheckDynamicCall(cleanCode, issues, originalCode, partName);
            CheckNewWhenDuplicate(cleanCode, issues, originalCode, partName);
        }

        private void CheckRulesPart(string cleanCode, string originalCode, JArray issues, string partName)
        {
            // Future specific rules checks
        }

        private void CheckCommitInsideLoop(string cleanCode, JArray issues, string originalCode, string partName)
        {
            var forEachBlocks = Regex.Matches(cleanCode, @"(?is)\bfor\s+each\b\s*.*?\s*\bendfor\b", RegexOptions.Compiled);
            foreach (Match m in forEachBlocks)
            {
                if (Regex.IsMatch(m.Value, @"(?i)\bcommit\b", RegexOptions.Compiled))
                {
                    int line = GetLineNumber(originalCode, m.Index);
                    issues.Add(CreateIssue("GX001", "Commit inside loop", "Critical", "Avoid using Commit inside a For Each loop as it breaks the LUW and cursor.", "Commit", line, partName));
                }
            }
        }

        private void CheckUnfilteredLoop(string cleanCode, JArray issues, string originalCode, string partName)
        {
            var forEachBlocks = Regex.Matches(cleanCode, @"(?is)\bfor\s+each\b\s*.*?\s*\bendfor\b", RegexOptions.Compiled);
            foreach (Match m in forEachBlocks)
            {
                string block = m.Value;
                if (!Regex.IsMatch(block, @"(?i)\bwhere\b|\bdefined\s+by\b", RegexOptions.Compiled))
                {
                    int line = GetLineNumber(originalCode, m.Index);
                    issues.Add(CreateIssue("GX002", "Unfiltered loop", "Critical", "Full table scan detected. Consider adding a 'where' clause.", "For Each", line, partName));
                }
            }
        }

        private void CheckSleepWait(string cleanCode, JArray issues, string originalCode, string partName)
        {
            var matches = Regex.Matches(cleanCode, @"(?i)\b(?:sleep|wait)\s*\(\s*\d+\s*\)", RegexOptions.Compiled);
            foreach (Match m in matches)
            {
                int line = GetLineNumber(originalCode, m.Index);
                issues.Add(CreateIssue("GX003", "Blocking call", "Warning", "Sleep/Wait calls block server threads. Use with caution in web environments.", m.Value, line, partName));
            }
        }

        private void CheckDynamicCall(string cleanCode, JArray issues, string originalCode, string partName)
        {
            var matches = Regex.Matches(cleanCode, @"(?i)\b(?:call|udp)\s*\(\s*&\w+\s*.*?\)", RegexOptions.Compiled);
            foreach (Match m in matches)
            {
                int line = GetLineNumber(originalCode, m.Index);
                issues.Add(CreateIssue("GX004", "Dynamic call", "Warning", "Call via variable breaks the call tree. Use literal names if possible.", m.Value, line, partName));
            }
        }

        private void CheckVariableUsageInObject(KBObject obj, JArray issues, string partName)
        {
            var varPart = obj.Parts.Get<VariablesPart>();
            if (varPart == null) return;

            string varContent = VariableInjector.GetVariablesAsText(obj);
            
            string allCode = "";
            foreach (var p in obj.Parts)
                if (p is ISource s) allCode += (s.Source ?? "") + "\n";
            
            string cleanAllCode = StripComments(allCode);

            foreach (var v in varPart.Variables)
            {
                if (new[] { "Pgmname", "Pgmdesc", "Mode", "Time", "Today", "UserId", "Output", "Page", "Line", "ReturnCode" }
                    .Contains(v.Name, StringComparer.OrdinalIgnoreCase)) continue;

                if (!Regex.IsMatch(cleanAllCode, @"&" + Regex.Escape(v.Name) + @"\b", RegexOptions.IgnoreCase))
                {
                    int line = 0;
                    var defMatch = Regex.Match(varContent, @"(?i)\b" + Regex.Escape(v.Name) + @"\b", RegexOptions.Compiled);
                    if (defMatch.Success) line = GetLineNumber(varContent, defMatch.Index);

                    issues.Add(CreateIssue("GX008", "Unused variable", "Warning", $"Variable '&{v.Name}' is defined but never used.", "&" + v.Name, line, "Variables"));
                }
            }
        }

        private void CheckSubroutines(string cleanCode, JArray issues, string originalCode, string partName)
        {
            var subDefinitions = Regex.Matches(cleanCode, @"(?is)\bsub\s+'([^']+)'(.*?)\bendsub\b", RegexOptions.Compiled);
            var subCalls = Regex.Matches(cleanCode, @"(?i)\bdo\s+'([^']+)'", RegexOptions.Compiled);

            var calledSubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in subCalls) calledSubs.Add(m.Groups[1].Value);

            foreach (Match m in subDefinitions)
            {
                string subName = m.Groups[1].Value;
                if (!calledSubs.Contains(subName))
                {
                    int line = GetLineNumber(originalCode, m.Index);
                    issues.Add(CreateIssue("GX009", "Unused subroutine", "Warning", $"Subroutine '{subName}' is defined but never called.", "Sub '" + subName + "'", line, partName));
                }
            }
        }

        private void CheckParmRule(string cleanCode, string objName, JArray issues, string partName)
        {
            if (string.IsNullOrWhiteSpace(cleanCode) || !Regex.IsMatch(cleanCode, @"(?i)\bparm\s*\(", RegexOptions.Compiled))
            {
                issues.Add(CreateIssue("GX006", "Parm rule missing", "Warning", "Procedure/WebPanel " + objName + " has no parameters defined in Rules.", "parm(...)", 1, partName));
            }
        }

        private void CheckNewWhenDuplicate(string cleanCode, JArray issues, string originalCode, string partName)
        {
            var newBlocks = Regex.Matches(cleanCode, @"(?is)\bnew\b\s*.*?\s*\bendnew\b", RegexOptions.Compiled);
            foreach (Match m in newBlocks)
            {
                if (!Regex.IsMatch(m.Value, @"(?i)\bwhen\s+duplicate\b", RegexOptions.Compiled))
                {
                    int line = GetLineNumber(originalCode, m.Index);
                    issues.Add(CreateIssue("GX005", "New without When Duplicate", "Info", "Consider adding 'when duplicate' to handle unique index collisions gracefully.", "New", line, partName));
                }
            }
        }

        private string StripComments(string code)
        {
            var blockComments = @"/\*(.*?)\*/";
            var lineComments = @"//(.*?)(?:\r?\n|$)";
            var strings = @"""((\\[^\n]|[^""\n])*)""|'((\\[^\n]|[^'\n])*)'";

            // SUBSTITUIÇÃO ELITE: Primeiro identifica strings, comentários de bloco e linha
            string result = Regex.Replace(code, string.Format("{0}|{1}|{2}", strings, blockComments, lineComments), me => {
                if (me.Value.StartsWith("\"") || me.Value.StartsWith("'"))
                    return me.Value; // É uma string, não mexer
                
                if (me.Value.StartsWith("/*"))
                    return new string(' ', me.Length); // Comentário de bloco -> espaços
                
                string suffix = me.Value.EndsWith("\n") ? "\n" : "";
                return new string(' ', me.Length - suffix.Length) + suffix;
            }, RegexOptions.Singleline);

            return result;
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
            try
            {
                var output = new global::Artech.Common.Diagnostics.OutputMessages();
                obj.Validate(output);

                foreach (var msg in output.OnlyMessages)
                {
                    if (msg is global::Artech.Common.Diagnostics.OutputError error)
                    {
                        int line = 1;
                        int column = 1;
                        // Determina a part lógica baseada no tipo de objeto
                        string part = (obj is Procedure) ? "Source" : ((obj is WebPanel) ? "Events" : "Logic");

                        if (error.Position is global::Artech.Common.Location.TextPosition textPos)
                        {
                            line = textPos.Line;
                            column = textPos.Char;
                        }

                        string severity = "Information";
                        if (error.Level == global::Artech.Common.Diagnostics.MessageLevel.Error) severity = "Error";
                        else if (error.Level == global::Artech.Common.Diagnostics.MessageLevel.Warning) severity = "Warning";

                        var issue = CreateIssue(error.ErrorCode, "SDK Validation", severity, error.Text, "", line, part);
                        issue["column"] = column;
                        issues.Add(issue);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Native SDK Validation failed: " + ex.Message);
            }
        }

        private JObject CreateIssue(string id, string title, string severity, string description, string snippet, int line, string part)
        {
            var issue = new JObject();
            issue["code"] = id;
            issue["title"] = title;
            issue["severity"] = severity;
            issue["description"] = description;
            issue["line"] = line;
            issue["snippet"] = snippet.Length > 200 ? snippet.Substring(0, 197).Trim() + "..." : snippet.Trim();
            issue["part"] = part;
            return issue;
        }
    }
}
