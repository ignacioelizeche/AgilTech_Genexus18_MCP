using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace GxMcp.Worker.Helpers
{
    public static class XmlEquivalence
    {
        public static bool AreEquivalent(string a, string b, out string diffSummary)
        {
            diffSummary = null;
            if (ReferenceEquals(a, b)) return true;
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return true;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                diffSummary = "One side empty.";
                return false;
            }

            XDocument da, db;
            try { da = XDocument.Parse(a, LoadOptions.PreserveWhitespace); }
            catch (Exception ex) { diffSummary = "Left parse error: " + ex.Message; return false; }
            try { db = XDocument.Parse(b, LoadOptions.PreserveWhitespace); }
            catch (Exception ex) { diffSummary = "Right parse error: " + ex.Message; return false; }

            return ElementsEqual(da.Root, db.Root, "/", out diffSummary);
        }

        private static bool ElementsEqual(XElement x, XElement y, string path, out string diff)
        {
            diff = null;
            if (x == null && y == null) return true;
            if (x == null || y == null) { diff = "Missing element at " + path; return false; }
            if (x.Name != y.Name) { diff = "Element name differs at " + path + ": '" + x.Name + "' vs '" + y.Name + "'"; return false; }

            var ax = x.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).ToList();
            var ay = y.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).ToList();
            if (ax.Count != ay.Count)
            {
                diff = "Attribute count differs at " + path + x.Name + " (" + ax.Count + " vs " + ay.Count + ")"
                       + ": left=[" + string.Join(",", ax.Select(a => a.Name.LocalName)) + "] right=[" + string.Join(",", ay.Select(a => a.Name.LocalName)) + "]";
                return false;
            }
            for (int i = 0; i < ax.Count; i++)
            {
                if (ax[i].Name != ay[i].Name)
                {
                    diff = "Attribute name differs at " + path + x.Name + ": '" + ax[i].Name + "' vs '" + ay[i].Name + "'";
                    return false;
                }
                if (!string.Equals(ax[i].Value, ay[i].Value, StringComparison.Ordinal))
                {
                    diff = "Attribute '" + ax[i].Name + "' differs at " + path + x.Name
                           + ": '" + Truncate(ax[i].Value) + "' vs '" + Truncate(ay[i].Value) + "'";
                    return false;
                }
            }

            var cx = SignificantChildren(x).ToList();
            var cy = SignificantChildren(y).ToList();
            if (cx.Count != cy.Count)
            {
                diff = "Child count differs at " + path + x.Name + " (" + cx.Count + " vs " + cy.Count + ")";
                return false;
            }

            for (int i = 0; i < cx.Count; i++)
            {
                var nx = cx[i];
                var ny = cy[i];
                if (nx.NodeType != ny.NodeType)
                {
                    diff = "Node type differs at " + path + x.Name + "[" + i + "]: " + nx.NodeType + " vs " + ny.NodeType;
                    return false;
                }

                if (nx is XElement ex2 && ny is XElement ey2)
                {
                    if (!ElementsEqual(ex2, ey2, path + x.Name + "/", out diff)) return false;
                }
                else if (nx is XText tx && ny is XText ty)
                {
                    var vx = (tx.Value ?? string.Empty).Trim();
                    var vy = (ty.Value ?? string.Empty).Trim();
                    if (!string.Equals(vx, vy, StringComparison.Ordinal))
                    {
                        diff = "Text differs at " + path + x.Name + "[" + i + "]: '" + Truncate(vx) + "' vs '" + Truncate(vy) + "'";
                        return false;
                    }
                }
            }
            return true;
        }

        private static IEnumerable<XNode> SignificantChildren(XElement e)
        {
            foreach (var n in e.Nodes())
            {
                if (n is XText t)
                {
                    if (string.IsNullOrWhiteSpace(t.Value)) continue;
                    yield return t;
                }
                else if (n is XComment) continue;
                else yield return n;
            }
        }

        private static string Truncate(string s)
        {
            if (s == null) return string.Empty;
            return s.Length <= 80 ? s : s.Substring(0, 80) + "…";
        }
    }
}
