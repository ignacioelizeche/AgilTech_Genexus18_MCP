using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using System.Reflection;

namespace GxMcp.Worker.Parsers
{
    public class SdtDslParser : IDslParser
    {
        private static readonly Guid SDT_STRUCTURE_PART = Guid.Parse("8597371d-1941-4c12-9c17-48df9911e2f3");

        public void Serialize(KBObject obj, StringBuilder sb)
        {
            if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
            {
                KBObject sdt = obj;
                KBObjectPart structure = null;
                
                // Find structure part: match by descriptor name, class name, or GUID
                foreach (KBObjectPart part in sdt.Parts)
                {
                    try {
                        string descName = part.TypeDescriptor?.Name ?? "";
                        string className = part.GetType().Name;
                        if (descName.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            descName.Equals("Structure", StringComparison.OrdinalIgnoreCase) ||
                            className.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            part.Type == SDT_STRUCTURE_PART)
                        { 
                            structure = part; 
                            break; 
                        }
                    } catch { }
                }
                
                // Fallback: duck typing - any part with Root
                if (structure == null)
                {
                    foreach (KBObjectPart part in sdt.Parts)
                    {
                        try {
                            dynamic dp = part;
                            if (dp.Root != null)
                            { structure = part; break; }
                        } catch { }
                    }
                }

                if (structure != null)
                {
                    dynamic ds = structure;
                    // DEEP DISCOVERY
                    try {
                        var partProps = new List<string>();
                        foreach (var p in structure.GetType().GetProperties()) partProps.Add(p.Name);
                        Logger.Debug($"[SDT PART DISCOVERY] Type: {structure.GetType().FullName} | Props: {string.Join(",", partProps)}");
                    } catch { }

                    // Try to find the root level (Root or StructureRoot)
                    dynamic root = null;
                    try { root = ds.Root; } catch { try { root = ds.StructureRoot; } catch { } }

                    if (root != null)
                    {
                        // Try all known collections for SDT levels
                        try { foreach (dynamic child in root.Items) SerializeLevel(child, sb, 0); } 
                        catch {
                            try { foreach (dynamic child in root.Children) SerializeLevel(child, sb, 0); }
                            catch {
                                try { foreach (dynamic child in root.InternalItems) SerializeLevel(child, sb, 0); }
                                catch { }
                            }
                        }
                    }
                }
                else
                {
                    Logger.Error($"SdtDslParser: Could not find structure part for SDT {obj.Name}");
                }
            }
        }

        public void Parse(KBObject obj, string text)
        {
            if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
            {
                KBObjectPart structure = null;
                foreach (KBObjectPart p in obj.Parts)
                {
                    if (p.Type == SDT_STRUCTURE_PART)
                    {
                        structure = p;
                        break;
                    }
                }

                if (structure != null)
                {
                    var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                    .Where(l => !string.IsNullOrWhiteSpace(l))
                                    .ToList();
                    var parsedNodes = DslParserUtils.ParseLinesIntoNodes(lines);
                    dynamic ds = structure;
                    SyncSDTNodes(ds.Root, parsedNodes);
                }
            }
        }

        private void SerializeLevel(dynamic level, StringBuilder sb, int indent)
        {
            string indentStr = new string(' ', indent * 4);
            string collectionMarker = "";
            try { collectionMarker = level.IsCollection ? " Collection" : ""; } catch { }
            
            bool isLeaf = true;
            try { isLeaf = level.IsLeafItem; } catch { }
            
            if (!isLeaf)
            {
                sb.AppendLine($"{indentStr}{level.Name}{collectionMarker}");
                sb.AppendLine($"{indentStr}{{");
                try { foreach (dynamic child in level.Items) SerializeLevel(child, sb, indent + 1); } catch { }
                sb.AppendLine($"{indentStr}}}");
            }
            else
            {
                string typeStr = "Unknown";
                try { typeStr = level.Type != null ? level.Type.ToString() : "Unknown"; } catch { }
                sb.AppendLine($"{indentStr}{level.Name} : {typeStr}{collectionMarker}");
            }
        }

        private void SyncSDTNodes(dynamic node, List<DslParserUtils.ParsedNode> parsedNodes)
        {
            // REFLECTION DISCOVERY (Log once per session)
            try {
                var props = new List<string>();
                foreach (PropertyInfo p in node.GetType().GetProperties()) props.Add(p.Name);
                var methods = new List<string>();
                foreach (MethodInfo m in node.GetType().GetMethods()) methods.Add(m.Name);
                Logger.Debug($"[SDT DISCOVERY] Node: {node.GetType().FullName} | Props: {string.Join(",", props)} | Methods: {string.Join(",", methods)}");
            } catch { }

            // Try to find the collection of items (Items or Children or Levels or Elements)
            dynamic items = null;
            try { items = node.Items; } catch { try { items = node.Children; } catch { try { items = node.Levels; } catch { try { items = node.Elements; } catch { } } } }
            if (items == null) return;

            var existingItems = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
            foreach (dynamic child in items) { existingItems[child.Name] = child; }

            var toRemove = new List<dynamic>();
            foreach (dynamic child in items) {
                if (!parsedNodes.Any(p => p.Name.Equals(child.Name, StringComparison.OrdinalIgnoreCase))) toRemove.Add(child);
            }
            foreach (dynamic dead in toRemove) { try { items.Remove(dead); } catch {} }

            foreach (var pNode in parsedNodes)
            {
                dynamic targetChild = null;
                if (existingItems.TryGetValue(pNode.Name, out var existing)) targetChild = existing;
                else
                {
                    // Discovery for SDTItem/SDTLevel type
                    Type sdtItemType = null;
                    string[] namespaces = { "Artech.Genexus.Common.Parts", "Artech.Genexus.Common.Objects", "Artech.Genexus.Common" };
                    foreach (var ns in namespaces) {
                        sdtItemType = node.GetType().Assembly.GetType($"{ns}.SDTItem") ?? 
                                      node.GetType().Assembly.GetType($"{ns}.SDTLevel");
                        if (sdtItemType != null) break;
                    }

                    if (sdtItemType != null) {
                        try {
                            targetChild = Activator.CreateInstance(sdtItemType, new object[] { node });
                            targetChild.Name = pNode.Name;
                            items.Add(targetChild);
                        } catch { }
                    }
                }

                if (targetChild != null)
                {
                    try { targetChild.IsCollection = pNode.IsCollection; } catch { }
                    if (pNode.IsCompound) SyncSDTNodes(targetChild, pNode.Children);
                    else
                    {
                        try {
                            Type eDBType = targetChild.GetType().Assembly.GetType("Artech.Genexus.Common.eDBType");
                            if (pNode.TypeStr.StartsWith("Numeric", StringComparison.OrdinalIgnoreCase)) targetChild.Type = Enum.Parse(eDBType, "NUMERIC");
                            else if (pNode.TypeStr.StartsWith("Char", StringComparison.OrdinalIgnoreCase)) targetChild.Type = Enum.Parse(eDBType, "VARCHAR");
                            else if (pNode.TypeStr.StartsWith("Date", StringComparison.OrdinalIgnoreCase)) targetChild.Type = Enum.Parse(eDBType, "DATE");
                            else if (pNode.TypeStr.StartsWith("Bool", StringComparison.OrdinalIgnoreCase)) targetChild.Type = Enum.Parse(eDBType, "Boolean");
                            else targetChild.Type = Enum.Parse(eDBType, "VARCHAR");
                        } catch { }
                    }
                }
            }
        }
    }
}
