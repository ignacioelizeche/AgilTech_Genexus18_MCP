using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class FormatService
    {
        private static readonly string[] Keywords = {
            "For Each", "EndFor", "If", "Else", "EndIf", "Do Case", "Case", "Otherwise", "EndCase",
            "Sub", "EndSub", "Do", "New", "EndNew", "Where", "Order", "Defined By", "Optimized",
            "Using", "When Duplicate", "When None", "Return", "Exit", "Call", "Udp", "Commit", "Rollback"
        };

        private static readonly string[] BlockStarters = {
            "For Each", "If", "Do Case", "New", "Sub", "Case", "Otherwise"
        };

        private static readonly string[] BlockEnders = {
            "EndFor", "EndIf", "EndCase", "EndNew", "EndSub", "Case", "Otherwise"
        };

        public string Format(string code)
        {
            try
            {
                if (string.IsNullOrEmpty(code)) return "{\"formatted\": \"\"}";

                string[] lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                List<string> result = new List<string>();
                int indentLevel = 0;
                const string indentStr = "\t"; // GeneXus standard usually uses Tabs, but we can stick to what the user prefers or standard Tabs

                foreach (string rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line))
                    {
                        result.Add("");
                        continue;
                    }

                    // 1. Keyword Normalization
                    line = NormalizeKeywords(line);

                    // 2. Determine Indent Shift (Current Line)
                    bool isEnder = IsBlockEnder(line);
                    if (isEnder) indentLevel = Math.Max(0, indentLevel - 1);

                    // 3. Apply Indentation
                    string formattedLine = "";
                    for (int i = 0; i < indentLevel; i++) formattedLine += indentStr;
                    formattedLine += line;
                    result.Add(formattedLine);

                    // 4. Determine Indent Shift (Next Line)
                    bool isStarter = IsBlockStarter(line);
                    if (isStarter) indentLevel++;
                }

                var json = new JObject();
                json["formatted"] = string.Join("\n", result);
                return json.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"Formatting failed: " + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string NormalizeKeywords(string line)
        {
            foreach (var kw in Keywords)
            {
                // We use regex to match whole word only
                string pattern = @"(?i)\b" + Regex.Escape(kw) + @"\b";
                line = Regex.Replace(line, pattern, kw);
            }
            return line;
        }

        private bool IsBlockStarter(string line)
        {
            foreach (var starter in BlockStarters)
            {
                if (line.StartsWith(starter, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private bool IsBlockEnder(string line)
        {
            foreach (var ender in BlockEnders)
            {
                if (line.StartsWith(ender, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
