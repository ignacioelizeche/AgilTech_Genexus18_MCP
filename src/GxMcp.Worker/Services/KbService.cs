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
        private BuildService _buildService;
        private readonly IndexCacheService _indexCacheService;
        private readonly VectorService _vectorService = new VectorService();

        // Progress Tracking
        private static int _processedCount = 0;
        private static int _totalCount = 0;
        private static bool _isIndexing = false;
        private static string _currentStatus = "";

        private static dynamic _kb;
        private static bool _isOpenInProgress = false;
        private static readonly object _kbLock = new object();

        public KbService(IndexCacheService indexCacheService)
        {
            _indexCacheService = indexCacheService;
        }

        public void SetBuildService(BuildService bs) { _buildService = bs; }

        public IndexCacheService GetIndexCache()
        {
            return _indexCacheService;
        }

        public bool IsInitializing => _isOpenInProgress;

        public dynamic GetKB()
        {
            lock (_kbLock) 
            { 
                if (_kb == null && !_isOpenInProgress)
                {
                    string kbPath = Environment.GetEnvironmentVariable("GX_KB_PATH");
                    if (!string.IsNullOrEmpty(kbPath))
                    {
                        Logger.Info($"Auto-opening KB in background: {kbPath}");
                        System.Threading.Tasks.Task.Run(() => OpenKB(kbPath));
                    }
                }
                return _kb; 
            }
        }

        public string OpenKB(string path)
        {
            lock (_kbLock)
            {
                if (_isOpenInProgress) return "{\"status\":\"In Progress\"}";
                _isOpenInProgress = true;
                
                if (_kb != null)
                {
                    try { if (string.Equals(_kb.Location, path, StringComparison.OrdinalIgnoreCase)) { _isOpenInProgress = false; return "{\"status\":\"Success\"}"; } } catch { }
                    try { _kb.Close(); } catch { }
                }

                try {
                    Logger.Info($"Opening KB: {path}");
                    string oldDir = Directory.GetCurrentDirectory();
                    try {
                        string kbDir = Path.GetDirectoryName(path);
                        Directory.SetCurrentDirectory(kbDir);
                        
                        var options = new global::Artech.Architecture.Common.Objects.KnowledgeBase.OpenOptions(path);
                        _kb = global::Artech.Architecture.Common.Objects.KnowledgeBase.Open(options);
                        
                        Logger.Info($"KB opened successfully.");
                        return "{\"status\":\"Success\"}";
                    } finally { Directory.SetCurrentDirectory(oldDir); _isOpenInProgress = false; }
                } catch (Exception ex) { 
                    Logger.Error($"ERROR opening KB: {ex.Message}");
                    _kb = null;
                    _isOpenInProgress = false;
                    return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
                }
            }
        }

        public string BulkIndex()
        {
            Logger.Info("BulkIndex() requested.");
            if (_isIndexing) return "{\"status\":\"Already in progress\"}";

            Program.BackgroundQueue.Enqueue(() => {
                try {
                    dynamic kb = GetKB();
                    if (kb == null) { _isIndexing = false; return; }
                    
                    _isIndexing = true;
                    _processedCount = 0;
                    _totalCount = 0;

                    var objects = new List<global::Artech.Architecture.Common.Objects.KBObject>();
                    foreach (global::Artech.Architecture.Common.Objects.KBObject obj in kb.DesignModel.Objects.GetAll()) {
                        objects.Add(obj);
                    }
                    _totalCount = objects.Count;
                    
                    var index = new SearchIndex { LastUpdated = DateTime.Now };
                    ProcessIndexChunk(index, objects, 0);
                } catch (Exception ex) {
                    _isIndexing = false;
                    _currentStatus = "Error: " + ex.Message;
                }
            });

            return "{\"status\":\"Started\"}";
        }

        private void ProcessIndexChunk(SearchIndex index, List<global::Artech.Architecture.Common.Objects.KBObject> objects, int startIndex)
        {
            const int chunkSize = 200;
            int endIndex = Math.Min(startIndex + chunkSize, objects.Count);

            var fastDataList = new List<dynamic>();
            for (int i = startIndex; i < endIndex; i++)
            {
                var obj = objects[i];
                try {
                    var calls = new List<string>();
                    foreach (var r in obj.GetReferences())
                    {
                        try {
                            var target = obj.Model.Objects.Get(r.To);
                            if (target != null) calls.Add(target.Name);
                        } catch { }
                    }

                    fastDataList.Add(new {
                        Guid = obj.Guid.ToString(),
                        Name = obj.Name,
                        Type = obj.TypeDescriptor.Name,
                        Description = obj.Description,
                        Parent = (obj.Parent != null && obj.Parent.Guid != obj.Guid) ? ((obj.Parent.TypeDescriptor.Name == "DesignModel") ? "Root Module" : obj.Parent.Name) : null,
                        Module = (obj.Module != null && obj.Module.Guid != obj.Guid) ? obj.Module.Name : null,
                        Calls = calls.Distinct().ToList()
                    });
                } catch { }
            }

            foreach (var data in fastDataList) {
                var entry = new SearchIndex.IndexEntry {
                    Guid = data.Guid, Name = data.Name, Type = data.Type,
                    Description = data.Description, Parent = data.Parent, Module = data.Module,
                    Calls = data.Calls
                };
                
                // Compute Embedding for Semantic Search
                string semanticText = $"{entry.Name} {entry.Type} {entry.Description}";
                entry.Embedding = _vectorService.ComputeEmbedding(semanticText);

                index.Objects[string.Format("{0}:{1}", entry.Type, entry.Name)] = entry;
            }

            _processedCount = endIndex;

            if (endIndex < objects.Count) {
                // PERIODIC SAVE: Every 1000 objects, flush to disk to avoid data loss on crash
                if (endIndex % 1000 == 0) {
                    _indexCacheService.UpdateIndex(index);
                    Logger.Info($"Intermediate Index Save: {endIndex} objects.");
                }
                Program.BackgroundQueue.Enqueue(() => ProcessIndexChunk(index, objects, endIndex));
            } else {
                _indexCacheService.UpdateIndex(index);
                _isIndexing = false;
                Logger.Info("Fast Bulk Indexing complete.");
            }
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
