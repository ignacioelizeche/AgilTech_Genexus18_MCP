using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;

namespace GxMcp.Worker.Helpers
{
    public static class StructureParser
    {
        public static string SerializeToText(KBObject obj)
        {
            var sb = new StringBuilder();
            try
            {
                if (obj is Transaction trn)
                {
                    SerializeLevel(trn.Structure.Root, sb, 0, true);
                }
                else if (obj is Table tbl)
                {
                    SerializeTable(tbl, sb);
                }
                else if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                {
                    dynamic sdt = obj;
                    dynamic structure = sdt.Parts.Get(Guid.Parse("8597371d-1941-4c12-9c17-48df9911e2f3"));
                    if (structure != null && structure.Root != null)
                    {
                        foreach (dynamic child in structure.Root.Children)
                        {
                            SerializeLevel(child, sb, 0, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("DSL Serialization Error: " + ex.ToString());
                sb.AppendLine("// Serialization Error: " + ex.Message);
            }
            return sb.ToString().Trim();
        }

        private static void SerializeTable(Table tbl, StringBuilder sb)
        {
            try
            {
                dynamic dStructure = ((dynamic)tbl).TableStructure;
                foreach (dynamic attr in dStructure.Attributes)
                {
                    string keyMarker = (bool)attr.IsKey ? "*" : "";
                    string typeStr = "Unknown";
                    string desc = "";
                    string formula = "";
                    bool isNullable = false;

                    try {
                        if (attr.Attribute != null) {
                            if (attr.Attribute.Type != null) typeStr = attr.Attribute.Type.ToString();
                            
                            // Include Length and Decimals
                            int len = attr.Attribute.Length;
                            int dec = attr.Attribute.Decimals;
                            if (len > 0)
                            {
                                if (dec > 0) typeStr += $"({len},{dec})";
                                else typeStr += $"({len})";
                            }

                            desc = attr.Attribute.Description?.ToString() ?? "";
                            formula = attr.Attribute.Formula?.ToString() ?? "";
                            
                            try {
                                int nVal = (int)attr.IsNullable;
                                isNullable = (nVal == 1); // True/Yes
                            } catch { }
                        }
                    } catch { }

                    var lineElements = new List<string>();
                    lineElements.Add(string.Format("{0}{1} : {2}", attr.Name, keyMarker, typeStr));
                    
                    if (!string.IsNullOrEmpty(desc) && !desc.Equals(attr.Name, StringComparison.OrdinalIgnoreCase)) {
                        lineElements.Add(string.Format("\"{0}\"", desc));
                    }
                    
                    if (!string.IsNullOrEmpty(formula)) {
                        lineElements.Add(string.Format("[Formula: {0}]", formula));
                    }
                    
                    if (isNullable) {
                        lineElements.Add("[Nullable]");
                    }

                    string extraInfo = lineElements.Count > 1 ? " // " + string.Join(", ", lineElements.Skip(1)) : "";
                    sb.AppendLine(string.Format("{0}{1}", lineElements[0], extraInfo));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("// Error serializing table: " + ex.Message);
            }
        }

        private static void SerializeLevel(dynamic level, StringBuilder sb, int indent, bool isTransaction)
        {
            string indentStr = new string(' ', indent * 4);
            
            if (isTransaction)
            {
                // Transaction Level
                if (indent > 0)
                {
                    sb.AppendLine($"{indentStr}{level.Name}");
                    sb.AppendLine($"{indentStr}{{");
                }

                // Attributes
                if (level.Attributes != null)
                {
                    foreach (dynamic attr in level.Attributes)
                    {
                        string keyMarker = attr.IsKey ? "*" : "";
                        string typeStr = "Unknown";
                        try {
                            // Try directly first (for SDT fields)
                            if (attr.Type != null) typeStr = attr.Type.ToString();
                        } catch {
                            // Map for TransactionAttribute
                            try {
                                if (attr.Attribute != null && attr.Attribute.Type != null) {
                                    typeStr = attr.Attribute.Type.ToString();
                                }
                            } catch { }
                        }

                        // Include Length and Decimals for relevant types
                        try {
                            if (attr.Attribute != null) {
                                int len = attr.Attribute.Length;
                                int dec = attr.Attribute.Decimals;
                                if (len > 0)
                                {
                                    if (dec > 0) typeStr += $"({len},{dec})";
                                    else typeStr += $"({len})";
                                }
                            }
                        } catch { }

                        string desc = "";
                        string formula = "";
                        bool isNullable = false;
                        try {
                            if (attr.Attribute != null) {
                                desc = attr.Attribute.Description != null ? attr.Attribute.Description.ToString() : "";
                                formula = attr.Attribute.Formula != null ? attr.Attribute.Formula.ToString() : "";
                                
                                // Proper Nullable check via SDK
                                dynamic pNullable = attr.Attribute.Properties.Get("Nullable");
                                if (pNullable != null) {
                                    string nVal = pNullable.ToString();
                                    isNullable = nVal.Equals("Yes", StringComparison.OrdinalIgnoreCase) || nVal.Equals("Nullable", StringComparison.OrdinalIgnoreCase);
                                }
                            }
                        } catch { }

                        // CONCISE LAYOUT: Name* : TYPE, "Description" [Formula] [Nullable]
                        var lineElements = new List<string>();
                        lineElements.Add(string.Format("{0}{1} : {2}", attr.Name, keyMarker, typeStr));
                        
                        if (!string.IsNullOrEmpty(desc) && !desc.Equals(attr.Name, StringComparison.OrdinalIgnoreCase)) {
                            lineElements.Add(string.Format("\"{0}\"", desc));
                        }
                        
                        if (!string.IsNullOrEmpty(formula)) {
                            lineElements.Add(string.Format("[Formula: {0}]", formula));
                        }
                        
                        if (isNullable) {
                            lineElements.Add("[Nullable]");
                        }

                        string extraInfo = lineElements.Count > 1 ? " // " + string.Join(", ", lineElements.Skip(1)) : "";
                        sb.AppendLine(string.Format("{0}{1}{2}{3}", indentStr, indent > 0 ? "    " : "", lineElements[0], extraInfo));
                    }
                }

                // SubLevels
                if (level.Levels != null)
                {
                    foreach (dynamic subLevel in level.Levels)
                    {
                        SerializeLevel(subLevel, sb, indent + 1, true);
                    }
                }

                if (indent > 0)
                {
                    sb.AppendLine($"{indentStr}}}");
                }
            }
            else
            {
                // SDT Level or Item
                string collectionMarker = level.IsCollection ? " Collection" : "";
                if (level.IsCompound)
                {
                    sb.AppendLine($"{indentStr}{level.Name}{collectionMarker}");
                    sb.AppendLine($"{indentStr}{{");
                    foreach (dynamic child in level.Children)
                    {
                        SerializeLevel(child, sb, indent + 1, false);
                    }
                    sb.AppendLine($"{indentStr}}}");
                }
                else
                {
                    string typeStr = level.Type != null ? level.Type.ToString() : "Unknown";
                    sb.AppendLine($"{indentStr}{level.Name} : {typeStr}{collectionMarker}");
                }
            }
        }

        public static void ParseFromText(KBObject obj, string text)
        {
            try
            {
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                .Where(l => !string.IsNullOrWhiteSpace(l))
                                .ToList();

                if (obj is Transaction trn)
                {
                    ParseTransactionStructure(trn.Structure.Root, lines, obj.Model);
                }
                else if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                {
                    dynamic sdt = obj;
                    dynamic structure = sdt.Parts.Get(Guid.Parse("8597371d-1941-4c12-9c17-48df9911e2f3"));
                    if (structure != null && structure.Root != null)
                    {
                        ParseSDTStructure(structure.Root, lines, obj.Model);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("DSL Parse Error: " + ex.ToString());
                throw;
            }
        }

        private static void ParseTransactionStructure(dynamic rootLevel, List<string> lines, KBModel model)
        {
            var parsedNodes = ParseLinesIntoNodes(lines);
            // In a real scenario, Transactions have a top-level matching their name, 
            // but the incoming DSL strips the top level out if it's the root itself.
            // Let's assume parsedNodes has attributes at level 0, or maybe the Root name at level 0.
            
            var targetNodes = parsedNodes;
            if (parsedNodes.Count == 1 && parsedNodes[0].IsCompound)
            {
                targetNodes = parsedNodes[0].Children;
            }

            SyncTransactionNodes(rootLevel, targetNodes, model);
        }

        private static void SyncTransactionNodes(dynamic sdkLevel, List<ParsedNode> parsedNodes, KBModel model)
        {
            var existingItems = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
            if (sdkLevel.Attributes != null)
            {
                foreach (dynamic attr in sdkLevel.Attributes) existingItems[attr.Name] = attr;
            }

            var toRemove = new List<dynamic>();
            if (sdkLevel.Attributes != null)
            {
                foreach (dynamic attr in sdkLevel.Attributes)
                {
                    if (!parsedNodes.Any(p => !p.IsCompound && p.Name.Equals(attr.Name, StringComparison.OrdinalIgnoreCase)))
                        toRemove.Add(attr);
                }
                foreach (dynamic dead in toRemove) { try { sdkLevel.Attributes.Remove(dead); } catch {} }
            }

            foreach (var pNode in parsedNodes)
            {
                if (pNode.IsCompound)
                {
                    // It's a Sub-Level
                    dynamic targetSubLevel = null;
                    if (sdkLevel.Levels != null)
                    {
                        foreach (dynamic subLvl in sdkLevel.Levels) {
                            if (subLvl.Name.Equals(pNode.Name, StringComparison.OrdinalIgnoreCase)) {
                                targetSubLevel = subLvl; break;
                            }
                        }
                    }

                    if (targetSubLevel == null)
                    {
                        Type levelType = sdkLevel.GetType().Assembly.GetType("Artech.Genexus.Common.Objects.TransactionLevel");
                        if (levelType != null)
                        {
                            targetSubLevel = Activator.CreateInstance(levelType, new object[] { sdkLevel });
                            targetSubLevel.Name = pNode.Name;
                            // sdkLevel.AddLevel(targetSubLevel) 
                            try { sdkLevel.Levels.Add(targetSubLevel); } catch { }
                        }
                    }

                    if (targetSubLevel != null)
                    {
                        SyncTransactionNodes(targetSubLevel, pNode.Children, model);
                    }
                }
                else
                {
                    if (existingItems.TryGetValue(pNode.Name, out var existing))
                    {
                        existing.IsKey = pNode.IsKey;
                    }
                    else
                    {
                        // Create transaction attribute
                        Type attrType = sdkLevel.GetType().Assembly.GetType("Artech.Genexus.Common.Objects.TransactionAttribute");
                        if (attrType != null)
                        {
                            try {
                                dynamic trnAttr = Activator.CreateInstance(attrType, new object[] { sdkLevel });
                                trnAttr.Name = pNode.Name;
                                trnAttr.IsKey = pNode.IsKey;
                                sdkLevel.Attributes.Add(trnAttr);
                            } catch { }
                        }
                    }
                }
            }
        }

        private static void ParseSDTStructure(dynamic rootLevel, List<string> lines, KBModel model)
        {
            var parsedNodes = ParseLinesIntoNodes(lines);
            SyncSDTNodes(rootLevel, parsedNodes);
        }

        class ParsedNode
        {
            public string Name { get; set; }
            public string TypeStr { get; set; }
            public bool IsCollection { get; set; }
            public bool IsCompound { get; set; }
            public bool IsKey { get; set; }
            public List<ParsedNode> Children { get; set; } = new List<ParsedNode>();
        }

        private static List<ParsedNode> ParseLinesIntoNodes(List<string> lines)
        {
            var rootNodes = new List<ParsedNode>();
            var stack = new Stack<(ParsedNode Node, int Indent)>();

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                int indent = line.TakeWhile(c => c == ' ').Count();
                string trimmed = line.Trim();
                
                int commentIndex = trimmed.IndexOf("//");
                if (commentIndex >= 0)
                {
                    trimmed = trimmed.Substring(0, commentIndex).Trim();
                }
                if (string.IsNullOrEmpty(trimmed)) continue;
                
                if (trimmed == "{") continue;
                if (trimmed == "}")
                {
                    if (stack.Count > 0) stack.Pop();
                    continue;
                }

                var node = new ParsedNode();
                
                bool collectionMarker = trimmed.EndsWith("Collection", StringComparison.OrdinalIgnoreCase);
                if (collectionMarker)
                {
                    trimmed = trimmed.Substring(0, trimmed.Length - 10).Trim();
                    node.IsCollection = true;
                }

                if (trimmed.EndsWith("*"))
                {
                    node.IsKey = true;
                    trimmed = trimmed.Substring(0, trimmed.Length - 1).Trim();
                }

                int colonIndex = trimmed.IndexOf(':');
                if (colonIndex > 0)
                {
                    node.Name = trimmed.Substring(0, colonIndex).Trim();
                    node.TypeStr = trimmed.Substring(colonIndex + 1).Trim();
                    node.IsCompound = false;
                }
                else
                {
                    node.Name = trimmed;
                    node.IsCompound = true;
                    // Lookahead: if it's not actually compound, it might just be a missing type
                    if (i + 1 < lines.Count && lines[i + 1].Trim() != "{")
                    {
                        node.IsCompound = false;
                        node.TypeStr = "Unknown";
                    }
                }

                while (stack.Count > 0 && stack.Peek().Indent >= indent)
                {
                    stack.Pop();
                }

                if (stack.Count == 0)
                {
                    rootNodes.Add(node);
                }
                else
                {
                    stack.Peek().Node.Children.Add(node);
                }

                if (node.IsCompound)
                {
                    stack.Push((node, indent));
                }
            }

            return rootNodes;
        }

        private static void SyncSDTNodes(dynamic sdkLevel, List<ParsedNode> parsedNodes)
        {
            // 1. Map existing children by Name
            var existingItems = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
            if (sdkLevel.Children != null)
            {
                foreach (dynamic child in sdkLevel.Children)
                {
                    existingItems[child.Name] = child;
                }
            }

            // 2. Clear sdkLevel.Children and re-add in the correct order based on parsedNodes
            // Due to limitations of dynamic list modification, we will create new items if we can't clear cleanly,
            // but normally we can just AddItem or RemoveItem. Since modifying dynamic enumerators is hard,
            // we will build a list of items to remove first.
            
            var toRemove = new List<dynamic>();
            foreach (dynamic child in sdkLevel.Children)
            {
                if (!parsedNodes.Any(p => p.Name.Equals(child.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    toRemove.Add(child);
                }
            }

            foreach (dynamic dead in toRemove)
            {
                // Most SDK collections support Remove
                try { sdkLevel.Children.Remove(dead); } catch {}
            }

            // 3. Update existing or Create new
            foreach (var pNode in parsedNodes)
            {
                dynamic targetChild = null;
                if (existingItems.TryGetValue(pNode.Name, out var existing))
                {
                    targetChild = existing;
                }
                else
                {
                    // Create new SDTItem via Activator
                    Type sdtItemType = sdkLevel.GetType().Assembly.GetType("Artech.Genexus.Common.Parts.SDTItem");
                    if (sdtItemType != null)
                    {
                        targetChild = Activator.CreateInstance(sdtItemType, new object[] { sdkLevel });
                        targetChild.Name = pNode.Name;
                        sdkLevel.Children.Add(targetChild);
                    }
                }

                if (targetChild != null)
                {
                    targetChild.IsCollection = pNode.IsCollection;
                    
                    if (pNode.IsCompound)
                    {
                        SyncSDTNodes(targetChild, pNode.Children);
                    }
                    else
                    {
                        // Parse Type string back to eDBType... simplified for now
                        try {
                            Type eDBType = targetChild.GetType().Assembly.GetType("Artech.Genexus.Common.eDBType");
                            if (pNode.TypeStr.StartsWith("Numeric", StringComparison.OrdinalIgnoreCase))
                                targetChild.Type = Enum.Parse(eDBType, "NUMERIC");
                            else if (pNode.TypeStr.StartsWith("Char", StringComparison.OrdinalIgnoreCase))
                                targetChild.Type = Enum.Parse(eDBType, "VARCHAR");
                            else if (pNode.TypeStr.StartsWith("Date", StringComparison.OrdinalIgnoreCase))
                                targetChild.Type = Enum.Parse(eDBType, "DATE");
                            else if (pNode.TypeStr.StartsWith("Bool", StringComparison.OrdinalIgnoreCase))
                                targetChild.Type = Enum.Parse(eDBType, "Boolean");
                            else
                                targetChild.Type = Enum.Parse(eDBType, "VARCHAR");
                        } catch { }
                    }
                }
            }
        }
    }
}
