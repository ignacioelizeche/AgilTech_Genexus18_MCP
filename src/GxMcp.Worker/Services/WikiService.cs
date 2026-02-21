using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;

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
                KBObject obj = _objectService.FindObject(target);
                if (obj == null) return "{\"error\": \"Object not found\"}";

                // Build markdown
                var md = new StringBuilder();
                md.AppendLine($"# {obj.Name}");
                md.AppendLine($"**Type:** {obj.TypeDescriptor.Name}");
                md.AppendLine($"**Description:** {obj.Description}");
                md.AppendLine($"**Updated:** {obj.LastUpdate:yyyy-MM-dd HH:mm}");
                md.AppendLine();

                // Mermaid Call Graph
                md.AppendLine("## Call Graph");
                md.AppendLine("```mermaid");
                md.AppendLine("graph TD");
                string selfNode = obj.Name.Replace(":", "_");
                md.AppendLine($"  {selfNode}[{obj.Name}]");
                
                var references = new System.Collections.Generic.List<string>();
                foreach(var refLink in obj.GetReferences())
                {
                    // Filter: Only callable objects (Prc, Trn, WebPanel)
                    // We need to resolve the target object from the Link
                    // Note: In SDK, references are links. Getting the target might be expensive if not careful.
                    // Simplified: We assume we can get the name from the reference or resolve it lightly.
                    try {
                        var targetObj = refLink.To != null ? obj.Model.Objects.Get(refLink.To) : null;
                        if (targetObj != null && (targetObj is Procedure || targetObj is Transaction || targetObj is WebPanel))
                        {
                            string targetName = targetObj.Name;
                            string targetNode = targetName.Replace(":", "_");
                            md.AppendLine($"  {selfNode} --> {targetNode}[{targetName}]");
                            references.Add(targetName);
                        }
                    } catch {}
                }
                md.AppendLine("```");
                md.AppendLine();

                // ER Diagram for Transactions
                if (obj is Transaction trn)
                {
                    md.AppendLine("## Entity Relationship");
                    md.AppendLine("```mermaid");
                    md.AppendLine("erDiagram");
                    md.AppendLine($"  {trn.Name} {{");
                    foreach(var attr in trn.Structure.Root.Attributes)
                    {
                        string keyMarker = attr.IsKey ? "PK" : "";
                        md.AppendLine($"    {attr.Name} {keyMarker}");
                    }
                    md.AppendLine("  }");
                    md.AppendLine("```");
                    md.AppendLine();
                }

                md.AppendLine("## Source Code");
                md.AppendLine("```genexus");
                string source = _objectService.GetObjectSource(target) ?? "// No source available";
                md.AppendLine(source.Length > 5000 ? source.Substring(0, 5000) + "\n// ... (truncated)" : source);
                md.AppendLine("```");

                // Save
                string docsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "docs");
                if (!Directory.Exists(docsDir)) Directory.CreateDirectory(docsDir);
                string filePath = Path.Combine(docsDir, $"{target.Replace(":", "_")}.md");
                File.WriteAllText(filePath, md.ToString(), Encoding.UTF8);

                string depsJson = "[" + string.Join(",", references.Distinct().Select(c => "\"" + CommandDispatcher.EscapeJsonString(c) + "\"")) + "]";

                return "{\"status\": \"Documentation generated\", \"file\": \"" + CommandDispatcher.EscapeJsonString(filePath) + "\","
                     + "\"dependencies\": " + depsJson + ","
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
