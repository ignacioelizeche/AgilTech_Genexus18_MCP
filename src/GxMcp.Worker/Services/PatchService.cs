using System;
using System.Linq;
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

        public string ApplyPatch(string target, string partName, string operation, string content, string context = null, int expectedCount = 1)
        {
            try
            {
                // 1. Read current content
                string currentSource = _objectService.ReadObjectSource(target, partName);
                if (currentSource.StartsWith("{\"error\"")) return currentSource;
                
                var json = Newtonsoft.Json.Linq.JObject.Parse(currentSource);
                string text = json["source"]?.ToString();
                if (text == null) return "{\"error\": \"Could not retrieve source for part: " + partName + "\"}";

                // Normalize line endings for consistent matching
                string normalizedText = text.Replace("\r\n", "\n");
                string normalizedContext = context?.Replace("\r\n", "\n");
                string normalizedContent = content.Replace("\r\n", "\n");

                // 2. Apply Transformation
                string newText = normalizedText;
                switch (operation?.ToLower())
                {
                    case "replace":
                        if (string.IsNullOrEmpty(normalizedContext)) return "{\"error\": \"'context' (old_string) is required for Replace.\"}";
                        
                        // Check for ambiguity
                        int occurrences = CountOccurrences(normalizedText, normalizedContext);
                        if (occurrences == 0) return "{\"error\": \"Context not found. Ensure whitespace and indentation match exactly.\"}";
                        if (occurrences > 1 && expectedCount == 1) 
                            return "{\"error\": \"Ambiguous match: found " + occurrences + " occurrences. Provide more context or set expectedCount.\"}";
                        if (expectedCount > 1 && occurrences != expectedCount)
                            return "{\"error\": \"Expected " + expectedCount + " occurrences but found " + occurrences + ".\"}";

                        newText = normalizedText.Replace(normalizedContext, normalizedContent);
                        break;

                    case "insert_after":
                        if (string.IsNullOrEmpty(normalizedContext)) return "{\"error\": \"'context' (anchor) is required for Insert_After.\"}";
                        int anchorCount = CountOccurrences(normalizedText, normalizedContext);
                        if (anchorCount == 0) return "{\"error\": \"Anchor context not found.\"}";
                        if (anchorCount > 1) return "{\"error\": \"Ambiguous anchor: found " + anchorCount + " occurrences.\"}";

                        int idx = normalizedText.IndexOf(normalizedContext);
                        newText = normalizedText.Insert(idx + normalizedContext.Length, "\n" + normalizedContent);
                        break;

                    case "append":
                        // PREVENT DUPLICATED EVENTS: Check if content looks like logic blocks (Event, Sub, etc.)
                        var blockMatches = Regex.Matches(normalizedContent, @"^(?:Event|Sub|Rule)\s+(.*?)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                        foreach (Match m in blockMatches)
                        {
                            string blockHeader = m.Value.Trim();
                            if (normalizedText.IndexOf(blockHeader, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                return "{\"error\": \"Conflict detected: '" + blockHeader + "' already exists in the object. " +
                                       "GeneXus does not allow duplicate blocks for the same event or subroutine. " +
                                       "Use 'Insert_After' to add logic inside the existing block or 'Replace' to modify it.\"}";
                            }
                        }
                        newText = normalizedText.TrimEnd() + "\n" + normalizedContent;
                        break;

                    default:
                        return "{\"error\": \"Unknown operation: " + operation + "\"}";
                }

                // 3. Write Back (re-normalize to CRLF for GeneXus if needed, or keep as is)
                string finalCode = newText.Replace("\n", Environment.NewLine);
                return _writeService.WriteObject(target, partName, finalCode);
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private int CountOccurrences(string text, string pattern)
        {
            int count = 0;
            int i = 0;
            while ((i = text.IndexOf(pattern, i)) != -1)
            {
                i += pattern.Length;
                count++;
            }
            return count;
        }
    }
}
