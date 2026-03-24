using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using System.Xml;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class NavigationService
    {
        private readonly KbService _kbService;

        public NavigationService(KbService kbService)
        {
            _kbService = kbService;
        }

        public string GetNavigation(string targetName)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                Logger.Info($"GetNavigation START: {targetName}");

                string nvgPath = FindNavigationFile(targetName);
                if (nvgPath == null) return "{\"error\": \"Navigation report not found for '" + targetName + "'. Make sure the object is specified.\"}";

                Logger.Info($"GetNavigation file resolved for {targetName}: {nvgPath}");

                var xmlSettings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    IgnoreComments = true,
                    IgnoreWhitespace = true,
                    CloseInput = true
                };

                XDocument doc = LoadNavigationDocument(nvgPath, xmlSettings, targetName);

                var result = new JObject();
                result["name"] = targetName;
                
                var levels = new JArray();
                foreach (var level in doc.Descendants("Level"))
                {
                    var levelObj = new JObject();
                    levelObj["number"] = (int?)level.Element("LevelNumber");
                    levelObj["type"] = level.Element("LevelType")?.Value;
                    levelObj["line"] = (int?)level.Element("LevelBeginRow");
                    
                    var baseTable = level.Element("BaseTable")?.Element("Table");
                    if (baseTable != null)
                    {
                        levelObj["baseTable"] = baseTable.Element("TableName")?.Value;
                        levelObj["baseTableDescription"] = baseTable.Element("Description")?.Value;
                    }

                    levelObj["index"] = level.Element("IndexName")?.Value;
                    
                    var order = new JArray();
                    var orderEl = level.Element("Order");
                    if (orderEl != null)
                    {
                        foreach (var att in orderEl.Elements("Attribute"))
                            order.Add(att.Element("AttriName")?.Value);
                    }
                    levelObj["order"] = order;

                    var optWhere = level.Element("OptimizedWhere");
                    bool hasOptimization = optWhere != null && optWhere.Elements().Any();
                    levelObj["isOptimized"] = hasOptimization;

                    levels.Add(levelObj);
                }

                result["levels"] = levels;

                var warnings = new JArray();
                foreach (var w in doc.Descendants("Warning"))
                    warnings.Add(w.Element("Message")?.Value);
                result["warnings"] = warnings;

                Logger.Info($"GetNavigation SUCCESS: {targetName} in {sw.ElapsedMilliseconds}ms");
                return result.ToString();
            }
            catch (Exception ex)
            {
                Logger.Error($"GetNavigation ERROR for {targetName}: {ex.Message}");
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string FindNavigationFile(string targetName)
        {
            var kb = _kbService.GetKB();
            if (kb == null) return null;

            string kbPath = kb.Location;
            if (File.Exists(kbPath))
            {
                kbPath = Path.GetDirectoryName(kbPath);
            }

            if (string.IsNullOrWhiteSpace(kbPath) || !Directory.Exists(kbPath))
            {
                return null;
            }

            var specFolders = Directory.EnumerateDirectories(kbPath, "GXSPC*", SearchOption.TopDirectoryOnly)
                                       .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase);

            foreach (var specFolder in specFolders)
            {
                foreach (var genFolder in Directory.EnumerateDirectories(specFolder, "GEN*", SearchOption.TopDirectoryOnly))
                {
                    string genPath = Path.Combine(genFolder, "NVG");
                    if (!Directory.Exists(genPath))
                    {
                        continue;
                    }

                    string fullPath = Path.Combine(genPath, targetName + ".xml");
                    if (File.Exists(fullPath)) return fullPath;
                }
            }

            return null;
        }

        private static XDocument LoadNavigationDocument(string nvgPath, XmlReaderSettings xmlSettings, string targetName)
        {
            try
            {
                using (var stream = new FileStream(nvgPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = XmlReader.Create(stream, xmlSettings))
                {
                    return XDocument.Load(reader, LoadOptions.None);
                }
            }
            catch (XmlException ex) when (LooksLikeLegacySingleByteEncoding(ex))
            {
                Logger.Info($"GetNavigation fallback decoding for {targetName}: retrying as Windows-1252.");
                string xmlText = Encoding.GetEncoding(1252).GetString(File.ReadAllBytes(nvgPath));
                using (var stringReader = new StringReader(xmlText))
                using (var reader = XmlReader.Create(stringReader, xmlSettings))
                {
                    return XDocument.Load(reader, LoadOptions.None);
                }
            }
        }

        private static bool LooksLikeLegacySingleByteEncoding(XmlException ex)
        {
            if (ex == null || string.IsNullOrWhiteSpace(ex.Message))
            {
                return false;
            }

            string message = ex.Message;
            return message.IndexOf("codifica", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("encoding", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
