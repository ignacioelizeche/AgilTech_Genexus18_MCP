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
                    string.Equals(part.TypeDescriptor?.Name, "WebForm", StringComparison.OrdinalIgnoreCase) ||
                    part.GetType().Name.IndexOf("WebForm", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static string ReadEditableXml(KBObject obj)
        {
            var part = GetWebFormPart(obj);
            if (part == null)
            {
                return string.Empty;
            }

            try
            {
                dynamic dPart = part;
                var document = dPart.Document as XmlDocument;
                if (document?.DocumentElement == null)
                {
                    return string.Empty;
                }

                return XDocument.Parse(document.OuterXml).ToString();
            }
            catch
            {
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
