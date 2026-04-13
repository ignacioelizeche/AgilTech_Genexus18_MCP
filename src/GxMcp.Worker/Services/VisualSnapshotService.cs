using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Xml.Linq;
using System.Linq;
using System.Collections.Generic;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class VisualSnapshotService
    {
        public string GetSnapshotBase64(string layoutXml)
        {
            if (string.IsNullOrEmpty(layoutXml)) return null;

            try
            {
                XDocument doc = XDocument.Parse(layoutXml);
                
                // ELITE: Identify dimensions. GeneXus report layouts don't have a fixed size, 
                // but we can calculate the bounding box of all elements.
                int maxWidth = 800;
                int maxHeight = 0;

                var textElements = doc.Descendants("TextBlock").ToList();
                var printBlocks = doc.Descendants().Where(e => e.Name.LocalName.Equals("PrintBlock", StringComparison.OrdinalIgnoreCase)).ToList();

                // Simple scaling factors (GeneXus internal units to pixels)
                float scale = 0.5f; 

                // Determine height based on PrintBlocks
                foreach (var pb in printBlocks)
                {
                    var heightAttr = pb.Attribute("Height");
                    if (heightAttr != null) maxHeight += (int)(int.Parse(heightAttr.Value) * scale) + 40;
                }

                if (maxHeight == 0) maxHeight = 1000;
                if (maxHeight > 4000) maxHeight = 4000; // Limit for memory safety

                using (Bitmap bmp = new Bitmap(maxWidth, maxHeight))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(30, 30, 30)); // Dark mode for premium feel
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                    Font titleFont = new Font("Segoe UI", 10, FontStyle.Bold);
                    Font textFont = new Font("Consolas", 8);
                    Pen blockPen = new Pen(Color.FromArgb(100, 100, 255), 1);
                    Brush textBrush = Brushes.White;
                    Brush labelBrush = Brushes.Yellow;

                    int currentY = 10;

                    foreach (var pb in printBlocks)
                    {
                        string pbName = pb.Attribute("Name")?.Value ?? "Unnamed Block";
                        int pbHeight = (int)(int.Parse(pb.Attribute("Height")?.Value ?? "0") * scale);

                        // Draw PrintBlock Header
                        g.DrawString($"[ PrintBlock: {pbName} ]", titleFont, Brushes.LightSkyBlue, 5, currentY);
                        currentY += 20;

                        // Draw PrintBlock Boundary
                        g.DrawRectangle(Pens.DimGray, 5, currentY, maxWidth - 20, pbHeight);

                        // Render elements inside this PrintBlock
                        foreach (var el in pb.Elements())
                        {
                            string localName = el.Name.LocalName;
                            if (string.Equals(localName, "Text", StringComparison.OrdinalIgnoreCase) || 
                                string.Equals(localName, "Variable", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(localName, "Attribute", StringComparison.OrdinalIgnoreCase))
                            {
                                int x = (int)(int.Parse(el.Attribute("Left")?.Value ?? "0") * scale);
                                int y = (int)(int.Parse(el.Attribute("Top")?.Value ?? "0") * scale);
                                int w = (int)(int.Parse(el.Attribute("Width")?.Value ?? "0") * scale);
                                int h = (int)(int.Parse(el.Attribute("Height")?.Value ?? "0") * scale);
                                string name = el.Attribute("Name")?.Value ?? "";
                                string text = el.Attribute("Caption")?.Value ?? (string.Equals(localName, "Text", StringComparison.OrdinalIgnoreCase) ? "" : "&" + name);

                                // Draw element box
                                g.DrawRectangle(blockPen, x + 10, currentY + y, w, h);
                                
                                // Draw text or label
                                string display = string.IsNullOrEmpty(text) ? name : text;
                                g.DrawString(display, textFont, string.Equals(localName, "Text", StringComparison.OrdinalIgnoreCase) ? labelBrush : textBrush, x + 12, currentY + y + 2);
                            }
                        }

                        currentY += pbHeight + 20;
                        if (currentY > maxHeight) break;
                    }

                    using (MemoryStream ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("VisualSnapshotService Error: " + ex.Message);
                return null;
            }
        }
    }
}
