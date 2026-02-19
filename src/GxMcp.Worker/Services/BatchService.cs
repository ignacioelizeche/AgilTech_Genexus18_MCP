using System;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace GxMcp.Worker.Services
{
    public class BatchService
    {
        private readonly ObjectService _objectService;
        private readonly BuildService _buildService;
        private readonly AnalyzeService _analyzeService;

        public BatchService(ObjectService objectService, BuildService buildService, AnalyzeService analyzeService)
        {
            _objectService = objectService;
            _buildService = buildService;
            _analyzeService = analyzeService;
        }

        public string Execute(string target, string action, string payload)
        {
            string batchDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "batch_buffer");

            try
            {
                switch (action?.ToLower())
                {
                    case "add":
                        return AddToBuffer(target, payload, batchDir);
                    case "commit":
                        return CommitBuffer(batchDir);
                    default:
                        return "{\"error\": \"Unknown batch action: " + action + ". Use Add or Commit.\"}";
                }
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string AddToBuffer(string target, string newCode, string batchDir)
        {
            if (string.IsNullOrEmpty(target)) return "{\"error\": \"Object name required for Add action\"}";

            if (!Directory.Exists(batchDir)) Directory.CreateDirectory(batchDir);

            string xmlContent = _objectService.GetObjectXml(target);
            if (xmlContent == null) return "{\"error\": \"Object not found: " + target + "\"}";

            var doc = new XmlDocument();
            doc.LoadXml(xmlContent);

            // Update source part if new code provided
            if (!string.IsNullOrEmpty(newCode))
            {
                const string SOURCE_GUID = "528d1c06-a9c2-420d-bd35-21dca83f12ff";
                var parts = doc.GetElementsByTagName("Part");
                foreach (XmlNode pn in parts)
                {
                    if (pn.Attributes?["type"]?.Value == SOURCE_GUID)
                    {
                        var src = pn.SelectSingleNode("Source");
                        if (src != null)
                        {
                            src.InnerXml = "<![CDATA[" + newCode + "]]>";
                        }
                        break;
                    }
                }
            }

            string safeName = target.Replace(":", "_");
            doc.Save(Path.Combine(batchDir, $"{safeName}.xml"));

            int count = Directory.GetFiles(batchDir, "*.xml").Length;
            var files = Directory.GetFiles(batchDir, "*.xml");
            var names = files.Select(f => "\"" + CommandDispatcher.EscapeJsonString(Path.GetFileNameWithoutExtension(f).Replace("_", ":")) + "\"");
            
            return "{\"status\": \"Added to buffer\", \"bufferCount\": " + count + ", \"bufferedObjects\": [" + string.Join(",", names) + "]}";
        }

        private string CommitBuffer(string batchDir)
        {
            if (!Directory.Exists(batchDir))
                return "{\"status\": \"Buffer is empty\"}";

            var files = Directory.GetFiles(batchDir, "*.xml");
            if (files.Length == 0)
                return "{\"status\": \"Buffer is empty\"}";

            // Merge all XMLs into one ExportFile
            var merged = new XmlDocument();
            merged.LoadXml("<?xml version='1.0' encoding='utf-8'?><ExportFile><KMW><MajorVersion>4</MajorVersion></KMW><Objects></Objects></ExportFile>");
            var objectsNode = merged.SelectSingleNode("//Objects");

            foreach (var f in files)
            {
                var frag = new XmlDocument();
                frag.Load(f);
                var objNode = frag.SelectSingleNode("//Object");
                if (objNode != null)
                {
                    var imported = merged.ImportNode(objNode, true);
                    objectsNode.AppendChild(imported);
                }
            }

            // Save, zip, import
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string xmlPath = Path.Combine(baseDir, "batch_commit.xml");
            string xpzPath = Path.Combine(baseDir, "batch_commit.xpz");
            string zipPath = Path.Combine(baseDir, "batch_commit.zip");

            merged.Save(xmlPath);

            if (File.Exists(zipPath)) File.Delete(zipPath);
            if (File.Exists(xpzPath)) File.Delete(xpzPath);
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(xmlPath, Path.GetFileName(xmlPath));
            }
            File.Move(zipPath, xpzPath);

            string kbPath = _buildService.GetKBPath();
            string gxDir = @"C:\Program Files (x86)\GeneXus\GeneXus18";
            string targetsFile = Path.Combine(baseDir, "batch_import.targets");
            string content = $@"<Project DefaultTargets='Import' xmlns='http://schemas.microsoft.com/developer/msbuild/2003'>
                <Import Project='{gxDir}\Genexus.Tasks.targets' />
                <Target Name='Import'>
                    <OpenKnowledgeBase Directory='{kbPath}' />
                    <Import File='{xpzPath}' />
                </Target>
            </Project>";
            File.WriteAllText(targetsFile, content, Encoding.UTF8);
            _buildService.RunMSBuild(targetsFile, "Import");

            // Cleanup
            File.Delete(xmlPath);
            File.Delete(xpzPath);
            File.Delete(targetsFile);
            Directory.Delete(batchDir, true);

            // Invalidate all cache as batch import might touch anything
            _objectService.ClearCache();

            var finalNames = files.Select(f => Path.GetFileNameWithoutExtension(f).Replace("_", ":")).ToList();
            
            // Live Indexing: Trigger analysis for all imported objects
            foreach (var name in finalNames)
            {
                try {
                    _analyzeService.AnalyzeInternal(name);
                } catch (Exception ex) {
                    Console.Error.WriteLine($"[BatchService] Live Indexing Failed for {name}: {ex.Message}");
                }
            }

            var finalNamesJson = finalNames.Select(n => "\"" + CommandDispatcher.EscapeJsonString(n) + "\"");
            return "{\"status\": \"Batch committed\", \"objectCount\": " + files.Length + ", \"committedObjects\": [" + string.Join(",", finalNamesJson) + "]}";
        }
    }
}
