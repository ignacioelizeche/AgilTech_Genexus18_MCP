using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    public static class CodeParser
    {
        private static readonly Regex SectionRegex = new Regex(@"(?i)^\s*(?:Sub|Event)\s+(?:['""]?([\w\.\-]+)['""]?|'([^']+)'|""([^""]+)"")", RegexOptions.Multiline | RegexOptions.Compiled);

        public static List<string> GetSections(string code)
        {
            var sections = new List<string>();
            var subMatches = SectionRegex.Matches(code);
            foreach (Match m in subMatches)
            {
                if (m.Groups[1].Success) sections.Add(m.Groups[1].Value);
                else if (m.Groups[2].Success) sections.Add(m.Groups[2].Value);
                else if (m.Groups[3].Success) sections.Add(m.Groups[3].Value);
            }
            return sections;
        }

        public static (int start, int end) GetSectionRange(string code, string sectionName)
        {
            string escaped = Regex.Escape(sectionName);
            var pattern = @"(?i)^\s*(?:Sub|Event)\s+(?:['""]?" + escaped + @"['""]?|'" + escaped + @"'|""" + escaped + @""")";
            var match = Regex.Match(code, pattern, RegexOptions.Multiline | RegexOptions.Compiled);
            
            if (!match.Success) return (-1, -1);

            int start = match.Index;
            string endPattern = "";
            
            string line = match.Value.Trim();
            if (line.StartsWith("Sub", StringComparison.OrdinalIgnoreCase))
                endPattern = @"(?i)^\s*EndSub\b";
            else
                endPattern = @"(?i)^\s*EndEvent\b";

            var endMatch = Regex.Match(code.Substring(start), endPattern, RegexOptions.Multiline | RegexOptions.Compiled);
            if (!endMatch.Success) return (start, code.Length);

            return (start, start + endMatch.Index + endMatch.Length);
        }
    }
}
