using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public sealed class SemanticOpsService
    {
        public string Apply(string xml, string objectKind, IEnumerable<SemanticOp> ops)
        {
            var doc = XDocument.Parse(xml);
            foreach (var op in ops)
                Dispatch(doc, objectKind, op);
            return doc.ToString(SaveOptions.DisableFormatting);
        }

        private static void Dispatch(XDocument doc, string kind, SemanticOp op)
        {
            switch (op.Op)
            {
                case "set_attribute" when kind == "Transaction":
                    SetAttribute(doc, op);
                    break;
                case "add_attribute" when kind == "Transaction":
                    AddAttribute(doc, op);
                    break;
                case "remove_attribute" when kind == "Transaction":
                    RemoveAttribute(doc, op);
                    break;
                case "add_rule" when kind == "Transaction" || kind == "Procedure" || kind == "WebPanel":
                    AddRule(doc, op);
                    break;
                case "remove_rule" when kind == "Transaction" || kind == "Procedure" || kind == "WebPanel":
                    RemoveRule(doc, op);
                    break;
                case "set_property":
                    SetProperty(doc, op);
                    break;
                default:
                    throw new UsageException("usage_error",
                        "op '" + op.Op + "' not supported for " + kind);
            }
        }

        private static void SetAttribute(XDocument doc, SemanticOp op)
        {
            string name = op.Args["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                throw new UsageException("usage_error", "set_attribute: name required");

            var attr = doc.Descendants("Attribute")
                .FirstOrDefault(a => (string)a.Element("Name") == name);
            if (attr == null)
                throw new UsageException("usage_error",
                    "attribute '" + name + "' not found");

            string type = op.Args["type"]?.ToString();
            if (type != null)
                attr.SetElementValue("Type", type);
        }

        private static void AddAttribute(XDocument doc, SemanticOp op)
        {
            string name = op.Args["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                throw new UsageException("usage_error", "add_attribute: name required");
            string type = op.Args["type"]?.ToString();
            if (string.IsNullOrEmpty(type))
                throw new UsageException("usage_error", "add_attribute: type required");

            var structure = doc.Descendants("Structure").FirstOrDefault();
            if (structure == null)
                throw new UsageException("usage_error", "add_attribute: <Structure> not found");

            structure.Add(new XElement("Attribute",
                new XElement("Name", name),
                new XElement("Type", type)));
        }

        private static void RemoveAttribute(XDocument doc, SemanticOp op)
        {
            string name = op.Args["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                throw new UsageException("usage_error", "remove_attribute: name required");

            var attr = doc.Descendants("Attribute")
                .FirstOrDefault(a => (string)a.Element("Name") == name);
            if (attr == null)
                throw new UsageException("usage_error",
                    "attribute '" + name + "' not found");
            attr.Remove();
        }

        private static void AddRule(XDocument doc, SemanticOp op)
        {
            string text = op.Args["text"]?.ToString();
            if (string.IsNullOrEmpty(text))
                throw new UsageException("usage_error", "add_rule: text required");

            var rules = doc.Descendants("Rules").FirstOrDefault();
            if (rules == null)
            {
                rules = new XElement("Rules");
                doc.Root?.Add(rules);
            }
            rules.Add(new XElement("Rule", new XElement("Text", text)));
        }

        private static void RemoveRule(XDocument doc, SemanticOp op)
        {
            var rulesContainer = doc.Descendants("Rules").FirstOrDefault();
            if (rulesContainer == null)
                throw new UsageException("usage_error", "remove_rule: <Rules> not found");

            var rules = rulesContainer.Elements("Rule").ToList();
            var indexToken = op.Args["index"];
            if (indexToken != null)
            {
                int idx = (int)indexToken;
                if (idx < 0 || idx >= rules.Count)
                    throw new UsageException("usage_error",
                        "remove_rule: index " + idx + " out of range");
                rules[idx].Remove();
                return;
            }

            string match = op.Args["match"]?.ToString();
            if (string.IsNullOrEmpty(match))
                throw new UsageException("usage_error", "remove_rule: match or index required");

            var target = rules.FirstOrDefault(r =>
            {
                string t = (string)r.Element("Text");
                return t != null && t.Contains(match);
            });
            if (target == null)
                throw new UsageException("usage_error",
                    "remove_rule: no rule matching '" + match + "'");
            target.Remove();
        }

        private static void SetProperty(XDocument doc, SemanticOp op)
        {
            string path = op.Args["path"]?.ToString();
            if (string.IsNullOrEmpty(path))
                throw new UsageException("usage_error", "set_property: path required");
            string value = op.Args["value"]?.ToString() ?? "";
            string name = path.TrimStart('/');
            var elem = doc.Root?.Element(name);
            if (elem == null)
                throw new UsageException("usage_error",
                    "set_property: path '" + path + "' not found");
            elem.Value = value;
        }
    }
}
