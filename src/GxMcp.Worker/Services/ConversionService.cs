using System;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class ConversionService
    {
        private readonly ObjectService _objectService;

        public ConversionService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        public string TranslateTo(string targetName, string targetLanguage)
        {
            try {
                if (string.IsNullOrWhiteSpace(targetName)) return "{\"error\":\"Object name is required\"}";
                if (string.IsNullOrWhiteSpace(targetLanguage)) return "{\"error\":\"Target language is required\"}";

                var obj = _objectService.FindObject(targetName);
                if (obj == null) return "{\"error\":\"Object not found\"}";

                string result = "";
                if (targetLanguage.Equals("csharp", StringComparison.OrdinalIgnoreCase) || targetLanguage.Equals("cs", StringComparison.OrdinalIgnoreCase))
                {
                    result = TranslateToCSharp(obj);
                }
                else if (targetLanguage.Equals("typescript", StringComparison.OrdinalIgnoreCase) || targetLanguage.Equals("ts", StringComparison.OrdinalIgnoreCase))
                {
                    result = TranslateToTypeScript(obj);
                }
                else
                {
                    return "{\"error\":\"Language " + targetLanguage + " not supported yet.\"}";
                }

                return "{\"status\":\"Success\", \"code\":\"" + CommandDispatcher.EscapeJsonString(result) + "\"}";
            } catch (Exception ex) {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string TranslateToCSharp(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine();
            sb.AppendLine($"namespace GeneXus.Generated.{obj.Module?.Name ?? "Root"}");
            sb.AppendLine("{");
            if (obj is Transaction trn) {
                sb.AppendLine($"    public class {obj.Name}");
                sb.AppendLine("    {");
                foreach (var attr in trn.Structure.Root.Attributes) {
                    string type = MapGxTypeToCSharp(attr.Attribute.Type.ToString());
                    sb.AppendLine($"        public {type} {attr.Name} {{ get; set; }}");
                }
                sb.AppendLine("    }");
            }
            else if (obj is SDT sdt) {
                sb.AppendLine($"    public class {obj.Name}");
                sb.AppendLine("    {");
                // Recursive SDT mapping simplified
                sb.AppendLine("        // SDT structure conversion...");
                sb.AppendLine("    }");
            }
            else if (obj is Procedure prc) {
                sb.AppendLine($"    public class {obj.Name}Handler");
                sb.AppendLine("    {");
                sb.AppendLine("        public void Execute()");
                sb.AppendLine("        {");
                sb.AppendLine("            // Original Source:");
                var procPart = prc.Parts.Get<ProcedurePart>();
                if (procPart != null) {
                    foreach (var line in procPart.Source.Split('\n')) {
                        sb.AppendLine($"            // {line.Trim()}");
                    }
                }
                sb.AppendLine("        }");
                sb.AppendLine("    }");
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        private string TranslateToTypeScript(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            var sb = new StringBuilder();
            if (obj is Transaction || obj is SDT) {
                sb.AppendLine($"export interface {obj.Name} {{");
                sb.AppendLine("}");
            }
            return sb.ToString();
        }

        private string MapGxTypeToCSharp(string gxType)
        {
            if (gxType.Contains("Numeric")) return "decimal";
            if (gxType.Contains("Character") || gxType.Contains("VarChar")) return "string";
            if (gxType.Contains("Date") || gxType.Contains("DateTime")) return "DateTime";
            if (gxType.Contains("Boolean")) return "bool";
            return "object";
        }
    }
}

