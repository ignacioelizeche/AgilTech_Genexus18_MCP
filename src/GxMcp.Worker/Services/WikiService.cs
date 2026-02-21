using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class WikiService
    {
        private readonly ObjectService _objectService;

        public WikiService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        public string Generate(string target)
        {
            try
            {
                string xmlContent = _objectService.GetObjectXml(target);
                if (xmlContent == null) return "{\"error\": \"Object not found\"}";

                var doc = new XmlDocument();
                doc.LoadXml(xmlContent);

                var objNode = doc.SelectSingleNode("//Object");
                string desc = objNode?.SelectSingleNode("Description")?.InnerText ?? "(no description)";
                string type = objNode?.Attributes?["type"]?.Value ?? "Unknown";

                // Map type GUID to friendly name
                string typeName = MapType(type);

                // Extract source code
                string sourceCode = "";
                var partNodes = doc.GetElementsByTagName("Part");
                foreach (XmlNode pn in partNodes)
                {
                    var src = pn.SelectSingleNode("Source");
                    if (src != null) sourceCode += src.InnerText + "\n";
                }

                // Extract comments
                var comments = Regex.Matches(sourceCode, @"(?s)/\*.*?\*/")
                    .Cast<Match>().Select(m => m.Value).ToList();

                // Extract calls
                var calls = Regex.Matches(sourceCode, @"(?i)(?:call|udp|submit)\s*\(\s*['""]?([\w\.]+)")
                    .Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();

                // Build markdown
                var md = new StringBuilder();
                md.AppendLine($"# {target}");
                md.AppendLine($"**Type:** {typeName}");
                md.AppendLine($"**Description:** {desc}");
                md.AppendLine($"**Generated:** {DateTime.Now:yyyy-MM-dd HH:mm}");
                md.AppendLine();

                md.AppendLine("## Relationship Diagram");
                md.AppendLine("```mermaid");
                md.AppendLine("graph TD");
                string selfNode = target.Replace(":", "_");
                md.AppendLine($"  {selfNode}[{target}]");
                foreach (var c in calls)
                {
                    string targetNode = c.Replace(":", "_").Replace(".", "_");
                    md.AppendLine($"  {selfNode} --> {targetNode}[{c}]");
                }
                md.AppendLine("```");
                md.AppendLine();

                if (calls.Count > 0)
                {
                    md.AppendLine("## Dependencies");
                    foreach (var c in calls) md.AppendLine($"- {c}");
                    md.AppendLine();
                }

                if (comments.Count > 0)
                {
                    md.AppendLine("## Business Rules (from comments)");
                    foreach (var c in comments) md.AppendLine(c);
                    md.AppendLine();
                }

                md.AppendLine("## Source Code");
                md.AppendLine("```genexus");
                md.AppendLine(sourceCode.Length > 5000 ? sourceCode.Substring(0, 5000) + "\n// ... (truncated)" : sourceCode);
                md.AppendLine("```");

                // Save to docs folder
                string docsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "docs");
                if (!Directory.Exists(docsDir)) Directory.CreateDirectory(docsDir);
                string safeName = target.Replace(":", "_");
                string filePath = Path.Combine(docsDir, $"{safeName}.md");
                File.WriteAllText(filePath, md.ToString(), Encoding.UTF8);

                string depsJson = "[" + string.Join(",", calls.Select(c => "\"" + CommandDispatcher.EscapeJsonString(c) + "\"")) + "]";
                string rulesJson = "[" + string.Join(",", comments.Select(c => "\"" + CommandDispatcher.EscapeJsonString(c) + "\"")) + "]";

                return "{\"status\": \"Documentation generated\", \"file\": \"" + CommandDispatcher.EscapeJsonString(filePath) + "\","
                     + "\"type\": \"" + typeName + "\","
                     + "\"dependencies\": " + depsJson + ","
                     + "\"rules\": " + rulesJson + ","
                     + "\"markdown\": \"" + CommandDispatcher.EscapeJsonString(md.ToString()) + "\"}";
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string GenerateBatch(string domainFilter)
        {
            try
            {
                var index = _objectService.GetIndex();
                if (index == null) return "{\"error\": \"Search Index not found\"}";

                var objects = index.Objects.Values
                    .Where(o => string.Equals(o.BusinessDomain, domainFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (objects.Count == 0) return "{\"error\": \"No objects found for domain: " + domainFilter + "\"}";

                var results = new JArray();
                foreach (var obj in objects)
                {
                    string res = Generate(obj.Name);
                    results.Add(JObject.Parse(res));
                }

                var finalResult = new JObject();
                finalResult["domain"] = domainFilter;
                finalResult["count"] = objects.Count;
                finalResult["results"] = results;

                return finalResult.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string MapType(string guid)
        {
            switch (guid)
            {
                case "1db606f2-af09-4cf9-a3b5-b481519d28f6": return "Transaction";
                case "c7086606-b07c-4218-bf76-915024b3c48f": return "Procedure";
                case "d8e1b5c4-a3f2-4b0e-9c6d-e7f8a9b0c1d2": return "WebPanel";
                default: return "Object (" + guid + ")";
            }
        }
    }
}
