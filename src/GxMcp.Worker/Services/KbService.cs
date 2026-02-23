using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class KbService
    {
        private readonly BuildService _buildService;
        private readonly IndexCacheService _indexCacheService;

        // Progress Tracking
        private static int _processedCount = 0;
        private static int _totalCount = 0;
        private static bool _isIndexing = false;
        private static string _currentStatus = "";

        private static dynamic _kb;
        private static readonly object _kbLock = new object();

        public KbService(BuildService buildService, IndexCacheService indexCacheService)
        {
            _buildService = buildService;
            _indexCacheService = indexCacheService;
        }

        public IndexCacheService GetIndexCache()
        {
            return _indexCacheService;
        }

        public dynamic GetKB()
        {
            lock (_kbLock) 
            { 
                if (_kb == null)
                {
                    string kbPath = Environment.GetEnvironmentVariable("GX_KB_PATH");
                    if (!string.IsNullOrEmpty(kbPath))
                    {
                        try 
                        { 
                            Logger.Info($"Auto-opening KB from environment: {kbPath}");
                            OpenKB(kbPath); 
                        } 
                        catch (Exception ex) 
                        { 
                            Logger.Error($"Auto-open failed: {ex.Message}");
                        }
                    }
                    else
                    {
                        Logger.Warn("GX_KB_PATH environment variable is empty.");
                    }
                }
                return _kb; 
            }
        }

        public void OpenKB(string path)
        {
            lock (_kbLock)
            {
                if (_kb != null)
                {
                    try { if (string.Equals(_kb.Location, path, StringComparison.OrdinalIgnoreCase)) return; } catch { }
                    try { _kb.Close(); } catch { }
                }

                try {
                    Logger.Info($"Opening KB: {path}");
                    string oldDir = Directory.GetCurrentDirectory();
                    try {
                        string kbDir = Path.GetDirectoryName(path);
                        Directory.SetCurrentDirectory(kbDir);
                        
                        // Use direct call with OpenOptions for stability
                        var options = new global::Artech.Architecture.Common.Objects.KnowledgeBase.OpenOptions(path);
                        
                        _kb = global::Artech.Architecture.Common.Objects.KnowledgeBase.Open(options);
                        
                        Logger.Info($"KB opened successfully. DesignModel: {_kb.DesignModel.Name}");
                    } finally { Directory.SetCurrentDirectory(oldDir); }
                } catch (Exception ex) { 
                    Logger.Error($"ERROR opening KB: {ex.Message}");
                    _kb = null;
                    throw;
                }
            }
        }

        public string BulkIndex()
        {
            if (_isIndexing) return "{\"status\":\"Already in progress\"}";

            System.Threading.Tasks.Task.Run(() => {
                try
                {
                    dynamic kb = GetKB();
                    if (kb == null) { _isIndexing = false; return; }
                    
                    Logger.Info("Bulk Indexing starting...");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    
                    _isIndexing = true;
                    _processedCount = 0;
                    _currentStatus = "Gathering objects...";
                    
                    var objects = new List<dynamic>();
                    try {
                        foreach (dynamic obj in kb.DesignModel.Objects.GetAll()) {
                            objects.Add(obj);
                        }
                    } catch (Exception ex) { Logger.Error("Gather error: " + ex.Message); }

                    _totalCount = objects.Count;
                    int processed = 0;

                    var index = new SearchIndex { LastUpdated = DateTime.Now };

                    _currentStatus = "Processing objects...";
                    foreach (dynamic obj in objects) {
                        try {
                            string parentName = null;
                            string moduleName = null;
                            try {
                                if (obj.Parent != null && obj.Parent.Guid != obj.Guid)
                                {
                                    string pType = obj.Parent.TypeDescriptor.Name;
                                    if (pType == "DesignModel")
                                        parentName = "Root Module";
                                    else if (pType == "Module" || pType == "Folder")
                                        parentName = obj.Parent.Name;
                                }
                            } catch { }
                            try {
                                if (obj.Module != null && obj.Module.Guid != obj.Guid)
                                    moduleName = obj.Module.Name;
                            } catch { }

                            var entry = new SearchIndex.IndexEntry {
                                Name = obj.Name,
                                Type = obj.TypeDescriptor.Name,
                                Description = obj.Description,
                                Parent = parentName,
                                Module = moduleName
                            };

                            try {
                                string t = entry.Type;
                                if (t == "Attribute") {
                                    entry.DataType = obj.Type.ToString();
                                    entry.Length = obj.Length;
                                    entry.Decimals = obj.Decimals;
                                } else if (t == "Table") {
                                    entry.RootTable = obj.Name;
                                }
                            } catch { }

                            if (obj.Name.Contains("_"))
                            {
                                entry.BusinessDomain = obj.Name.Split('_')[0];
                            }

                            try {
                                if (entry.Type == "Procedure" || entry.Type == "WebPanel")
                                {
                                    dynamic rulesPart = obj.Parts.Get(Guid.Parse("9b0a32a3-de6d-4be1-a4dd-1b85d3741534")); // Rules
                                    if (rulesPart != null)
                                    {
                                        string rSrc = rulesPart.Source;
                                        var parmMatch = System.Text.RegularExpressions.Regex.Match(rSrc, @"(?i)\bparm\s*\(.*?\)\s*;", System.Text.RegularExpressions.RegexOptions.Singleline);
                                        if (parmMatch.Success) entry.ParmRule = parmMatch.Value.Trim();
                                    }

                                    dynamic sourcePart = obj.Parts.Get(Guid.Parse("c5f0ef88-9ef8-4218-bf76-915024b3c48f")); // Source
                                    if (sourcePart != null)
                                    {
                                        string sSrc = sourcePart.Source;
                                        if (!string.IsNullOrEmpty(sSrc)) {
                                            var lines = sSrc.Split('\n');
                                            entry.SourceSnippet = string.Join("\n", lines.Take(5)).Trim();
                                        }
                                    }
                                }
                            } catch { }

                            try
                            {
                                foreach (dynamic reference in obj.GetReferences())
                                {
                                    try
                                    {
                                        dynamic targetKey = reference.To;
                                        string targetName = targetKey.Name;
                                        if (string.IsNullOrEmpty(targetName)) targetName = targetKey.ToString();

                                        if (string.IsNullOrEmpty(targetName)) continue;

                                        string targetType = targetKey.TypeDescriptor.Name;
                                        if (targetType == "Attribute" || targetType == "Table")
                                        {
                                            if (!entry.Tables.Contains(targetName)) entry.Tables.Add(targetName);
                                        }
                                        else
                                        {
                                            if (!entry.Calls.Contains(targetName)) entry.Calls.Add(targetName);
                                        }
                                    }
                                    catch { }
                                }
                            }
                            catch { }

                            index.Objects[string.Format("{0}:{1}", entry.Type, entry.Name)] = entry;
                            
                            processed++;
                            _processedCount = processed;
                            if (processed % 1000 == 0) {
                                Logger.Info(string.Format("Progress: {0}/{1} objects indexed...", processed, _totalCount));
                            }
                        } catch { }
                    }

                    _indexCacheService.UpdateIndex(index);
                    sw.Stop();
                    _isIndexing = false;
                    _processedCount = _totalCount;
                    _currentStatus = "Complete";
                    
                    Logger.Info($"Bulk Indexing complete. {_totalCount} objects in {sw.ElapsedMilliseconds}ms.");
                } catch (Exception ex) { 
                    _isIndexing = false;
                    Logger.Error($"BulkIndex Fatal: {ex.Message}");
                }
            });

            return "{\"status\":\"Started\"}";
        }

        public string GetIndexStatus()
        {
            var json = new Newtonsoft.Json.Linq.JObject();
            json["isIndexing"] = _isIndexing;
            json["total"] = _totalCount;
            json["processed"] = _processedCount;
            json["status"] = _currentStatus;
            
            if (_totalCount > 0)
                json["progress"] = (int)((_processedCount / (float)_totalCount) * 100);
            else
                json["progress"] = 0;

            return json.ToString();
        }
    }
}
