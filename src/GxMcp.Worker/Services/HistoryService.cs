using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class HistoryService
    {
        private readonly ObjectService _objectService;
        private readonly WriteService _writeService;

        public HistoryService(ObjectService objectService, WriteService writeService)
        {
            _objectService = objectService;
            _writeService = writeService;
        }

        public string Execute(string target, string action, int versionId = 0)
        {
            try
            {
                switch (action?.ToLower())
                {
                    case "list":
                        return ListRevisions(target);
                    case "get_source":
                        return GetVersionSource(target, versionId);
                    case "save":
                        return SaveSnapshot(target);
                    case "restore":
                        return RestoreSnapshot(target);
                    default:
                        return Models.McpResponse.Error("Unknown history action", target, action, "Supported actions are list, get_source, save and restore.");
                }
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string GetVersionSource(string target, int versionId)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null)
            {
                return Models.McpResponse.Error(
                    "Object not found",
                    target,
                    "Source",
                    "The requested object is not available in the active Knowledge Base."
                );
            }

            try
            {
                var versions = obj.GetVersions().Cast<global::Artech.Architecture.Common.Objects.KBObject>().ToList();
                var targetVersion = versions.FirstOrDefault(v => v.VersionId == versionId);

                if (targetVersion != null)
                {
                    var sourcePart = targetVersion.Parts.Cast<global::Artech.Architecture.Common.Objects.KBObjectPart>()
                                        .FirstOrDefault(p => p is global::Artech.Architecture.Common.Objects.ISource) 
                                        as global::Artech.Architecture.Common.Objects.ISource;

                    if (sourcePart != null)
                    {
                        string content = sourcePart.Source ?? "";
                        var result = new JObject();
                        result["source"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
                        result["isBase64"] = true;
                        result["versionId"] = versionId;
                        return result.ToString();
                    }
                }
                return "{\"error\": \"Version " + versionId + " not found or has no source code.\"}";
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to read version source: " + ex.Message);
                return "{\"error\": \"SDK Version access failed: " + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string ListRevisions(string target)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null)
            {
                return Models.McpResponse.Error(
                    "Object not found",
                    target,
                    null,
                    "The requested object is not available in the active Knowledge Base."
                );
            }

            var history = new JArray();
            try
            {
                var versions = obj.GetVersions().Cast<global::Artech.Architecture.Common.Objects.KBObject>();
                foreach (var rev in versions)
                {
                    history.Add(new JObject
                    {
                        ["version"] = rev.VersionId,
                        ["date"] = rev.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                        ["user"] = rev.UserName,
                        ["comment"] = rev.Comment
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to read revisions: " + ex.Message);
                return "{\"error\": \"SDK History access failed: " + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }

            return new JObject { ["history"] = history }.ToString();
        }

        private string SaveSnapshot(string target)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null) return Models.McpResponse.Error("Object not found", target);

            string histDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".history");
            if (!Directory.Exists(histDir)) Directory.CreateDirectory(histDir);
            
            string sourceJson = _objectService.ReadObjectSource(target, "Source", client: "mcp");
            if (sourceJson.Contains("\"error\"")) return sourceJson;

            var json = JObject.Parse(sourceJson);
            string code = json["source"] != null ? json["source"].ToString() : "";

            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            // Use canonical name: Type_Name
            string safeName = $"{obj.TypeDescriptor.Name}_{obj.Name}".Replace(":", "_").Replace(" ", "_");
            string filePath = Path.Combine(histDir, string.Format("{0}_{1}.txt", safeName, ts));
            File.WriteAllText(filePath, code, Encoding.UTF8);

            return "{\"status\": \"Snapshot saved\", \"file\": \"" + CommandDispatcher.EscapeJsonString(Path.GetFileName(filePath)) + "\", \"timestamp\": \"" + ts + "\", \"canonicalName\": \"" + safeName + "\"}";
        }

        private string RestoreSnapshot(string target)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null) return Models.McpResponse.Error("Object not found", target);

            string histDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".history");
            if (!Directory.Exists(histDir)) Directory.CreateDirectory(histDir);

            // Use canonical name: Type_Name
            string safeName = $"{obj.TypeDescriptor.Name}_{obj.Name}".Replace(":", "_").Replace(" ", "_");
            var files = Directory.GetFiles(histDir, $"{safeName}_*.txt")
                .OrderByDescending(f => f)
                .ToArray();

            if (files.Length == 0)
                return "{\"error\": \"No snapshots found for " + CommandDispatcher.EscapeJsonString(safeName) + " (Target: " + target + ")\"}";

            string lastFile = files.First();
            string code = File.ReadAllText(lastFile, Encoding.UTF8);

            return _writeService.WriteObject(target, "Source", code);
        }
    }
}
