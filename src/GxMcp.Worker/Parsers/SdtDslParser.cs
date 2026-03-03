using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Parsers
{
    public class SdtDslParser : IDslParser
    {
        private static readonly Guid SDT_STRUCTURE_PART = Guid.Parse("8597371d-1941-4c12-9c17-48df9911e2f3");

        public void Serialize(KBObject obj, StringBuilder sb)
        {
            if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
            {
                dynamic sdt = obj;
                dynamic structure = null;
                
                // Find structure part: match by descriptor name, class name, or GUID
                foreach (dynamic part in sdt.Parts)
                {
                    try {
                        string descName = part.TypeDescriptor?.Name ?? "";
                        string className = part.GetType().Name;
                        if (descName.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            descName.Equals("Structure", StringComparison.OrdinalIgnoreCase) ||
                            className.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0)
                        { structure = part; break; }
                    } catch { }
                }
                
                // Fallback: Parts.Get with known GUID
                if (structure == null)
                {
                    try { structure = sdt.Parts.Get(SDT_STRUCTURE_PART); } catch { }
                }
                
                // Fallback: duck typing - any part with Root.Children
                if (structure == null)
                {
                    foreach (dynamic part in sdt.Parts)
                    {
                        try {
                            if (part.Root != null && part.Root.Children != null)
                            { structure = part; break; }
                        } catch { }
                    }
                }

                if (structure != null && structure.Root != null)
                {
                    foreach (dynamic child in structure.Root.Items) SerializeLevel(child, sb, 0);
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
                dynamic sdt = obj;
                dynamic structure = sdt.Parts.Get(SDT_STRUCTURE_PART);
                if (structure != null && structure.Root != null)
                {
                    var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                    .Where(l => !string.IsNullOrWhiteSpace(l))
                                    .ToList();
                    var parsedNodes = DslParserUtils.ParseLinesIntoNodes(lines);
                    SyncSDTNodes(structure.Root, parsedNodes);
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

        private void SyncSDTNodes(dynamic sdkLevel, List<DslParserUtils.ParsedNode> parsedNodes)
        {
            var existingItems = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
            if (sdkLevel.Children != null) { foreach (dynamic child in sdkLevel.Children) existingItems[child.Name] = child; }

            var toRemove = new List<dynamic>();
            foreach (dynamic child in sdkLevel.Children) {
                if (!parsedNodes.Any(p => p.Name.Equals(child.Name, StringComparison.OrdinalIgnoreCase))) toRemove.Add(child);
            }
            foreach (dynamic dead in toRemove) { try { sdkLevel.Children.Remove(dead); } catch {} }

            foreach (var pNode in parsedNodes)
            {
                dynamic targetChild = null;
                if (existingItems.TryGetValue(pNode.Name, out var existing)) targetChild = existing;
                else
                {
                    Type sdtItemType = sdkLevel.GetType().Assembly.GetType("Artech.Genexus.Common.Parts.SDTItem");
                    if (sdtItemType != null) {
                        targetChild = Activator.CreateInstance(sdtItemType, new object[] { sdkLevel });
                        targetChild.Name = pNode.Name;
                        sdkLevel.Children.Add(targetChild);
                    }
                }

                if (targetChild != null)
                {
                    targetChild.IsCollection = pNode.IsCollection;
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
