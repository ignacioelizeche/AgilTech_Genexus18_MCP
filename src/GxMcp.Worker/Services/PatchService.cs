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

        public string ApplyPatch(string target, string partName, string operation, string content, string context = null)
        {
            try
            {
                // 1. Read current content
                string currentSource = _objectService.ReadObjectSource(target, partName);
                if (currentSource.StartsWith("{\"error\"")) return currentSource;
                
                var json = Newtonsoft.Json.Linq.JObject.Parse(currentSource);
                string text = json["source"]?.ToString();
                if (text == null) return "{\"error\": \"Could not retrieve source for part: " + partName + "\"}";

                // 2. Apply Transformation
                string newText = text;
                switch (operation?.ToLower())
                {
                    case "append":
                        newText = text + "\n" + content;
                        break;
                    
                    case "prepend":
                        newText = content + "\n" + text;
                        break;

                    case "replace":
                        if (string.IsNullOrEmpty(context)) return "{\"error\": \"Context (old_string) is required for Replace operation.\"}";
                        if (!text.Contains(context)) return "{\"error\": \"Context not found in source.\"}";
                        newText = text.Replace(context, content);
                        break;

                    case "insert_after":
                        if (string.IsNullOrEmpty(context)) return "{\"error\": \"Context (anchor) is required for Insert_After operation.\"}";
                        string pattern = Regex.Escape(context).Replace("\\ ", "\\s+");
                        var match = Regex.Match(text, pattern, RegexOptions.Multiline);
                        if (!match.Success) return "{\"error\": \"Anchor context not found.\"}";
                        newText = text.Insert(match.Index + match.Length, "\n" + content);
                        break;

                    default:
                        return "{\"error\": \"Unknown operation: " + operation + "\"}";
                }

                // 3. Write Back
                if (newText == text) return "{\"status\": \"No changes applied.\"}";
                
                return _writeService.WriteObject(target, partName, newText);
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }
    }
}
