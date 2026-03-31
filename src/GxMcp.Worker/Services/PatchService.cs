using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class PatchService
    {
        private readonly ObjectService _objectService;
        private readonly WriteService _writeService;

        public PatchService(ObjectService objectService, WriteService writeService)
        {
            _objectService = objectService;
            _writeService = writeService;
        }

        public string ApplyPatch(string target, string partName, string operation, string content, string context = null, int expectedCount = 1, string typeFilter = null)
        {
            try
            {
                // 1. Read current content (requesting 'mcp' client for plain text)
                string currentResponse = _objectService.ReadObjectSource(target, partName, client: "mcp", typeFilter: typeFilter);
                if (currentResponse.Contains("\"error\"")) return currentResponse;
                
                var json = Newtonsoft.Json.Linq.JObject.Parse(currentResponse);
                string originalSource = json["source"]?.ToString();
                if (originalSource == null) return "{\"error\": \"Could not retrieve source for part: " + partName + "\"}";

                // Normalize line endings for internal processing
                string workSource = originalSource.Replace("\r\n", "\n").Replace("\r", "\n");
                string workContext = context?.Replace("\r\n", "\n").Replace("\r", "\n");
                string workContent = content.Replace("\r\n", "\n").Replace("\r", "\n");

                var sourceLines = workSource.Split('\n');
                var contextLines = workContext?.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

                // 2. Matching Logic
                string updatedSource = null;

                switch (operation?.ToLower())
                {
                    case "replace":
                        if (string.IsNullOrEmpty(workContext)) return "{\"error\": \"'context' (old_string) is required for Replace.\"}";
                        updatedSource = TryReplace(sourceLines, contextLines, workContent, expectedCount, out string replaceError);
                        if (replaceError != null) return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(replaceError) + "\"}";
                        break;

                    case "insert_after":
                        if (string.IsNullOrEmpty(workContext)) return "{\"error\": \"'context' (anchor) is required for Insert_After.\"}";
                        updatedSource = TryInsertAfter(sourceLines, contextLines, workContent, out string insertError);
                        if (insertError != null) return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(insertError) + "\"}";
                        break;

                    case "append":
                        updatedSource = workSource.TrimEnd() + "\n" + workContent;
                        break;

                    default:
                        return "{\"error\": \"Unknown operation: " + operation + "\"}";
                }

                if (updatedSource == null)
                {
                    string dbg = "";
                    if (sourceLines.Length > 0 && contextLines?.Length > 0)
                    {
                        dbg = $" | Example: Source='{sourceLines[0].Trim()}' vs Context='{contextLines[0].Trim()}'";
                    }
                    Logger.Warn($"[PATCH] Match failed for {target}. Context: '{context}'{dbg}");
                    return "{\"error\": \"Context not found. Ensure the context matches a unique block in the source code. " + CommandDispatcher.EscapeJsonString(dbg) + "\"}";
                }

                // 3. Write Back (re-normalize to CRLF for GeneXus)
                string finalCode = updatedSource.Replace("\n", Environment.NewLine);
                return _writeService.WriteObject(target, partName, finalCode, typeFilter);
            }
            catch (Exception ex)
            {
                Logger.Error($"[PATCH] Error applying patch: {ex.Message}");
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string TryReplace(string[] sourceLines, string[] contextLines, string newContent, int expectedCount, out string error)
        {
            error = null;
            string source = string.Join("\n", sourceLines);
            string context = string.Join("\n", contextLines);
            
            // 1. Exact match attempt
            int exactCount = CountOccurrences(source, context);
            if (exactCount == expectedCount)
            {
                Logger.Info("[PATCH] Exact match found.");
                return source.Replace(context, newContent);
            }

            // 2. Fuzzy match attempt
            Logger.Info("[PATCH] Exact match failed or count mismatch (" + exactCount + " vs " + expectedCount + "). Attempting fuzzy match.");
            var indices = FindFuzzyMatches(sourceLines, contextLines);
            
            if (indices.Count == expectedCount)
            {
                int idx = indices[0];
                Logger.Info($"[PATCH] Fuzzy match found at line {idx}.");
                
                // Indentation Preservation
                string indentation = GetIndentation(sourceLines[idx]);
                string indentedContent = ApplyIndentation(newContent, indentation);

                var resultLines = new List<string>(sourceLines);
                resultLines.RemoveRange(idx, contextLines.Length);
                resultLines.Insert(idx, indentedContent);
                return string.Join("\n", resultLines);
            }

            if (indices.Count > 0)
            {
                error = $"Ambiguous patch: Found {indices.Count} fuzzy matches, but expected {expectedCount}. Please provide more context to uniquely identify the block.";
                return null;
            }

            return null;
        }

        private string TryInsertAfter(string[] sourceLines, string[] contextLines, string newContent, out string error)
        {
            error = null;
            string source = string.Join("\n", sourceLines);
            string context = string.Join("\n", contextLines);

            if (CountOccurrences(source, context) == 1)
            {
                int idx = source.IndexOf(context);
                return source.Insert(idx + context.Length, "\n" + newContent);
            }

            var indices = FindFuzzyMatches(sourceLines, contextLines);
            if (indices.Count == 1)
            {
                int idx = indices[0];
                string indentation = GetIndentation(sourceLines[idx]);
                string indentedContent = ApplyIndentation(newContent, indentation);
                
                var resultLines = new List<string>(sourceLines);
                resultLines.Insert(idx + contextLines.Length, indentedContent);
                return string.Join("\n", resultLines);
            }

            if (indices.Count > 1)
            {
                error = $"Ambiguous anchor: Found {indices.Count} fuzzy matches for the anchor. Please provide more context.";
            }

            return null;
        }

        private List<int> FindFuzzyMatches(string[] sourceLines, string[] targetLines)
        {
            var matches = new List<int>();
            for (int i = 0; i <= sourceLines.Length - targetLines.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < targetLines.Length; j++)
                {
                    if (!LinesMatchFuzzy(sourceLines[i + j], targetLines[j]))
                    {
                        match = false;
                        break;
                    }
                }
                if (match) matches.Add(i);
            }
            return matches;
        }

        private bool LinesMatchFuzzy(string s1, string s2)
        {
            // Normalize: trim and collapse multiples spaces
            string n1 = NormalizeWhitespace(s1);
            string n2 = NormalizeWhitespace(s2);
            return string.Equals(n1, n2, StringComparison.OrdinalIgnoreCase);
        }

        private string NormalizeWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            // Collapse all whitespace (including tabs and multiple spaces) into a single space
            return Regex.Replace(s.Trim(), @"\s+", " ");
        }

        private string GetIndentation(string line)
        {
            var match = Regex.Match(line, @"^(\s*)");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private string ApplyIndentation(string content, string indentation)
        {
            if (string.IsNullOrEmpty(indentation)) return content;
            var lines = content.Replace("\r\n", "\n").Split('\n');
            return string.Join("\n", lines.Select(l => indentation + l));
        }

        private int CountOccurrences(string text, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return 0;
            int count = 0, i = 0;
            while ((i = text.IndexOf(pattern, i)) != -1) { i += pattern.Length; count++; }
            return count;
        }
    }
}
