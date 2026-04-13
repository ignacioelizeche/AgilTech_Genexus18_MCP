using System;
using System.Linq;
using System.Collections.Generic;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;

namespace GxMcp.Worker.Structure
{
    public static class PartAccessor
    {
        public static bool MatchesSourcePart(string requestedPartName, string sourcePartName)
        {
            if (string.IsNullOrWhiteSpace(requestedPartName))
            {
                return false;
            }

            bool isEventsRequest = requestedPartName.Equals("Events", StringComparison.OrdinalIgnoreCase);
            bool isSourceAliasRequest =
                requestedPartName.Equals("Source", StringComparison.OrdinalIgnoreCase) ||
                requestedPartName.Equals("Code", StringComparison.OrdinalIgnoreCase);

            if (isEventsRequest)
            {
                return string.Equals(sourcePartName, "Events", StringComparison.OrdinalIgnoreCase);
            }

            if (!isSourceAliasRequest)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(sourcePartName))
            {
                return true;
            }

            return !string.Equals(sourcePartName, "Events", StringComparison.OrdinalIgnoreCase);
        }

        public static Guid GetPartGuid(string objType, string partName)
        {
            string p = partName.ToLower();

            if (objType.Equals("Procedure", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "source" || p == "code") return Guid.Parse("c5f0ef88-9ef8-4218-bf76-915024b3c48f");
                if (p == "rules") return Guid.Parse("9b0a32a3-de6d-4be1-a4dd-1b85d3741534");
                if (p == "variables") return Guid.Parse("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
                if (p == "help") return Guid.Parse("017ea008-6202-4468-a400-3f412c938473");
                if (p == "layout") return Guid.Parse("c414ed00-8cc4-4f44-8820-4baf93547173");
            }
            
            if (objType.Equals("WebPanel", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "events" || p == "source" || p == "code") return Guid.Parse("c44bd5ff-f918-415b-98e6-aca44fed84fa");
                if (p == "rules") return Guid.Parse("9b0a32a3-de6d-4be1-a4dd-1b85d3741534");
                if (p == "variables") return Guid.Parse("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
                if (p == "layout") return Guid.Parse("ad3ca970-19d0-44e1-a7b7-db05556e820c");
                if (p == "webform") return Guid.Parse("d24a58ad-57ba-41b7-9e6e-eaca3543c778");
            }

            if (objType.Equals("Transaction", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "structure") return Guid.Parse("1608677c-a7a2-4a00-8809-6d2466085a5a");
                if (p == "events" || p == "source" || p == "code") return Guid.Parse("c44bd5ff-f918-415b-98e6-aca44fed84fa");
                if (p == "rules") return Guid.Parse("9b0a32a3-de6d-4be1-a4dd-1b85d3741534");
                if (p == "variables") return Guid.Parse("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
                if (p == "layout" || p == "webform") return Guid.Parse("d24a58ad-57ba-41b7-9e6e-eaca3543c778");
            }

            if (objType.Equals("DataProvider", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "source" || p == "code") return Guid.Parse("91705646-6086-4f32-8871-08149817e754");
                if (p == "variables") return Guid.Parse("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
                if (p == "help") return Guid.Parse("017ea008-6202-4468-a400-3f412c938473");
            }

            if (objType.Equals("SDT", StringComparison.OrdinalIgnoreCase) || objType.Equals("StructuredDataType", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "structure" || p == "source") return Guid.Parse("8597371d-1941-4c12-9c17-48df9911e2f3");
            }

            // Defaults fallback
            switch (p)
            {
                case "source": return Guid.Parse("c5f0ef88-9ef8-4218-bf76-915024b3c48f");
                case "rules": return Guid.Parse("9b0a32a3-de6d-4be1-a4dd-1b85d3741534");
                case "events": return Guid.Parse("c44bd5ff-f918-415b-98e6-aca44fed84fa");
                case "variables": return Guid.Parse("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
                case "structure": return Guid.Parse("1608677c-a7a2-4a00-8809-6d2466085a5a");
                case "layout": return Guid.Parse("c414ed00-8cc4-4f44-8820-4baf93547173");
                case "webform": return Guid.Parse("d24a58ad-57ba-41b7-9e6e-eaca3543c778");
                case "patterninstance": return Guid.Parse("a51ced48-7bee-0001-ab12-04e9e32123d1");
                case "help": return Guid.Parse("017ea008-6202-4468-a400-3f412c938473");
                case "documentation": return Guid.Parse("26323631-6435-4235-3037-333036343530");
                default: return Guid.Empty;
            }
        }

        public static KBObjectPart GetPart(KBObject obj, string partName)
        {
            Guid partGuid = GetPartGuid(obj.TypeDescriptor.Name, partName);
            
            if (partGuid != Guid.Empty)
            {
                var part = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.Type == partGuid);
                if (part != null) return part;
            }

            // Dynamic Discovery Fallback
            foreach (KBObjectPart p in obj.Parts)
            {
                if (p.TypeDescriptor != null && p.TypeDescriptor.Name.Equals(partName, StringComparison.OrdinalIgnoreCase)) return p;
                if (p is ISource && MatchesSourcePart(partName, p.TypeDescriptor?.Name)) return p;
                if (p.GetType().Name.Equals("VariablesPart") && partName.Equals("Variables", StringComparison.OrdinalIgnoreCase)) return p;
            }

            if (partName.Equals("Source", StringComparison.OrdinalIgnoreCase) || partName.Equals("Code", StringComparison.OrdinalIgnoreCase))
            {
                return obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p is ISource);
            }

            return null;
        }

        public static string[] GetAvailableParts(KBObject obj)
        {
            if (obj == null)
            {
                return new string[0];
            }

            return obj.Parts
                .Cast<KBObjectPart>()
                .Select(GetDisplayPartName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string GetDisplayPartName(KBObjectPart part)
        {
            if (part == null)
            {
                return null;
            }

            if (part is ISource)
            {
                var sourceName = part.TypeDescriptor?.Name;
                if (string.Equals(sourceName, "Events", StringComparison.OrdinalIgnoreCase))
                {
                    return "Events";
                }

                return "Source";
            }

            if (part.GetType().Name.Equals("VariablesPart", StringComparison.OrdinalIgnoreCase))
            {
                return "Variables";
            }

            if (!string.IsNullOrWhiteSpace(part.TypeDescriptor?.Name))
            {
                return part.TypeDescriptor.Name;
            }

            return part.GetType().Name.Replace("Part", "");
        }
    }
}
