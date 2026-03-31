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

        public static List<string> Validate(string code)
        {
            var errors = new List<string>();
            var stack = new Stack<(string name, int line)>();
            
            string[] lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("//") || line.StartsWith("/*")) continue;

                // Handle single-line If (If ... EndIf)
                bool isSingleLineIf = Regex.IsMatch(line, @"(?i)^\s*If\b") && Regex.IsMatch(line, @"(?i)\bEndif\b");
                if (isSingleLineIf) continue;

                // Simple Block Matching
                if (Regex.IsMatch(line, @"(?i)^\s*If\b") && !line.Contains(";")) stack.Push(("If", i + 1));
                else if (Regex.IsMatch(line, @"(?i)^\s*Do\s+while\b", RegexOptions.IgnoreCase)) stack.Push(("Do While", i + 1));
                else if (Regex.IsMatch(line, @"(?i)^\s*Do\s+case\b", RegexOptions.IgnoreCase)) stack.Push(("Do Case", i + 1));
                else if (Regex.IsMatch(line, @"(?i)^\s*For\s+each\b", RegexOptions.IgnoreCase)) stack.Push(("For Each", i + 1));
                else if (Regex.IsMatch(line, @"(?i)^\s*For\b", RegexOptions.IgnoreCase)) stack.Push(("For Each", i + 1)); // Handle generic For as For Each for simpler stack
                else if (Regex.IsMatch(line, @"(?i)^\s*Sub\b", RegexOptions.IgnoreCase)) stack.Push(("Sub", i + 1));
                else if (Regex.IsMatch(line, @"(?i)^\s*Event\b", RegexOptions.IgnoreCase)) stack.Push(("Event", i + 1));

                else if (Regex.IsMatch(line, @"(?i)^\s*Endif\b", RegexOptions.IgnoreCase))
                {
                    if (stack.Count == 0 || stack.Peek().name != "If") errors.Add($"Line {i + 1}: 'Endif' without matching 'If'");
                    else stack.Pop();
                }
                else if (Regex.IsMatch(line, @"(?i)^\s*Enddo\b", RegexOptions.IgnoreCase))
                {
                    if (stack.Count == 0 || stack.Peek().name != "Do While") errors.Add($"Line {i + 1}: 'Enddo' without matching 'Do While'");
                    else stack.Pop();
                }
                else if (Regex.IsMatch(line, @"(?i)^\s*Endcase\b", RegexOptions.IgnoreCase))
                {
                    if (stack.Count == 0 || stack.Peek().name != "Do Case") errors.Add($"Line {i + 1}: 'Endcase' without matching 'Do Case'");
                    else stack.Pop();
                }
                else if (Regex.IsMatch(line, @"(?i)^\s*Endfor\b", RegexOptions.IgnoreCase))
                {
                    if (stack.Count == 0 || stack.Peek().name != "For Each") errors.Add($"Line {i + 1}: 'Endfor' without matching 'For Each'");
                    else stack.Pop();
                }
                else if (Regex.IsMatch(line, @"(?i)^\s*Endsub\b", RegexOptions.IgnoreCase))
                {
                    if (stack.Count == 0 || stack.Peek().name != "Sub") errors.Add($"Line {i + 1}: 'Endsub' without matching 'Sub'");
                    else stack.Pop();
                }
                else if (Regex.IsMatch(line, @"(?i)^\s*Endevent\b", RegexOptions.IgnoreCase))
                {
                    if (stack.Count == 0 || stack.Peek().name != "Event") errors.Add($"Line {i + 1}: 'Endevent' without matching 'Event'");
                    else stack.Pop();
                }
            }

            while (stack.Count > 0)
            {
                var top = stack.Pop();
                errors.Add($"Line {top.line}: '{top.name}' without matching End");
            }

            return errors;
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
