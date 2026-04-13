using System;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;

namespace GxMcp.Worker.Helpers
{
    public static class WebFormXmlHelper
    {
        public static bool IsVisualPart(string partName)
        {
            return string.Equals(partName, "Layout", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(partName, "WebForm", StringComparison.OrdinalIgnoreCase);
        }

        public static KBObjectPart GetWebFormPart(KBObject obj)
        {
            if (obj == null) return null;

            return obj.Parts
                .Cast<KBObjectPart>()
                .FirstOrDefault(part =>
                {
                    string name = part.TypeDescriptor?.Name ?? "";
                    string typeName = part.GetType().Name;
                    
                    // ELITE: In GeneXus Procedures, the 'Layout' part is often a ReportPart.
                    // We allow it here so that ObjectService can read it as Visual XML.
                    if (name.Equals("WebForm", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Layout", StringComparison.OrdinalIgnoreCase) ||
                        typeName.IndexOf("WebForm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        typeName.IndexOf("Report", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }

                    return false;
                });
        }

        public static string ReadEditableXml(KBObject obj)
        {
            var part = GetWebFormPart(obj);
            if (part == null)
            {
                Logger.Info($"[LayoutFix] GetWebFormPart returned NULL for {obj.Name}");
                return string.Empty;
            }

            Logger.Info($"[LayoutFix] Found visual part for {obj.Name}: {part.TypeDescriptor?.Name} (GUID: {part.Type})");

            // ELITE: If it's a ReportPart, we use the specialized ReportLayoutHelper.
            if (ReportLayoutHelper.IsReportPart(part) != null)
            {
                Logger.Info($"[LayoutFix] Part identified as Report for {obj.Name}. Reading via ReportLayoutHelper.");
                var xml = ReportLayoutHelper.ReadLayout(part);
                if (!string.IsNullOrEmpty(xml)) return xml;
                Logger.Info($"[LayoutFix] ReportLayoutHelper.ReadLayout returned empty/null for {obj.Name}");
            }

            try
            {
                dynamic dPart = part;
                var document = dPart.Document as XmlDocument;
                if (document?.DocumentElement == null)
                {
                    Logger.Info($"[LayoutFix] DocumentElement is NULL for {obj.Name}");
                    return string.Empty;
                }

                return XDocument.Parse(document.OuterXml).ToString();
            }
            catch (Exception ex)
            {
                Logger.Info($"[LayoutFix] ReadEditableXml error for {obj.Name}: {ex.Message}");
                try
                {
                    dynamic dPart = part;
                    return dPart.Document?.OuterXml ?? string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        public static string NormalizeEditableXmlInput(string xml, string partName)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                throw new ArgumentException("Visual XML payload is empty.");
            }

            string trimmed = xml.Trim();
            if (trimmed.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Layout writes require raw GxMultiForm XML, not preview HTML. Read part='Layout' again and edit the returned XML.");
            }

            var doc = XDocument.Parse(trimmed, LoadOptions.PreserveWhitespace);
            string rootName = doc.Root?.Name.LocalName ?? string.Empty;
            if (!string.Equals(rootName, "GxMultiForm", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    string.Format("Visual writes currently require the full GxMultiForm XML document. Received root '{0}' for part '{1}'.", rootName, partName ?? "Layout"));
            }

            return doc.ToString();
        }

        public static void ApplyEditableXml(KBObjectPart part, string xml)
        {
            if (part == null)
            {
                throw new ArgumentNullException(nameof(part));
            }

            string normalized = NormalizeEditableXmlInput(xml, part.TypeDescriptor?.Name);

            // ELITE: Support ReportPart persistence
            if (ReportLayoutHelper.IsReportPart(part) != null)
            {
                if (!ReportLayoutHelper.WriteLayout(part, normalized))
                {
                    throw new InvalidOperationException("Failed to write Report layout via reflection.");
                }
                return;
            }

            dynamic fallbackPart = part;
            XmlDocument existingDocument = fallbackPart.Document as XmlDocument;
            if (existingDocument == null)
            {
                throw new InvalidOperationException("The WebForm part does not expose an XML document.");
            }

            var xmlDocument = new XmlDocument();
            xmlDocument.LoadXml(normalized);
            existingDocument.RemoveAll();
            existingDocument.LoadXml(xmlDocument.OuterXml);
        }
    }
}
