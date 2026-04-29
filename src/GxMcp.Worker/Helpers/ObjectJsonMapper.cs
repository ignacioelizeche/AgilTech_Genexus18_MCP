using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Bidirectional XML ↔ canonical JSON mapping for GeneXus part XML.
    /// Coverage: top-level scalar children + Structure/Attribute arrays.
    /// Other shapes (Procedure source, WebPanel layout) are NOT covered — use mode:xml or mode:ops.
    /// </summary>
    public static class ObjectJsonMapper
    {
        public static JObject ToJson(string xml)
        {
            var doc = XDocument.Parse(xml);
            var root = doc.Root;
            var json = new JObject();
            if (root == null) return json;

            foreach (var child in root.Elements())
            {
                if (child.Name.LocalName == "Structure")
                {
                    var arr = new JArray();
                    foreach (var attr in child.Elements("Attribute"))
                    {
                        arr.Add(new JObject
                        {
                            ["name"] = (string)attr.Element("Name"),
                            ["type"] = (string)attr.Element("Type")
                        });
                    }
                    json["structure"] = arr;
                }
                else
                {
                    json[LowerFirst(child.Name.LocalName)] = child.Value;
                }
            }
            return json;
        }

        public static string ToXml(JObject json, string rootName)
        {
            var root = new XElement(rootName);
            foreach (var prop in json.Properties())
            {
                if (prop.Name == "structure" && prop.Value is JArray arr)
                {
                    var struc = new XElement("Structure");
                    foreach (var item in arr.OfType<JObject>())
                    {
                        struc.Add(new XElement("Attribute",
                            new XElement("Name", (string)item["name"]),
                            new XElement("Type", (string)item["type"])));
                    }
                    root.Add(struc);
                }
                else
                {
                    root.Add(new XElement(UpperFirst(prop.Name), prop.Value.ToString()));
                }
            }
            return root.ToString(SaveOptions.DisableFormatting);
        }

        private static string LowerFirst(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToLower(s[0]) + s.Substring(1);

        private static string UpperFirst(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);
    }
}
