using System;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Artech.Architecture.Common.Objects;

namespace GxMcp.Worker.Services
{
    public class KbService
    {
        private BuildService _buildService;
        private readonly IndexCacheService _indexCacheService;
        private readonly VectorService _vectorService = new VectorService();

        // Progress Tracking
        private static volatile int _processedCount = 0;
        private static volatile int _totalCount = 0;
        private static volatile bool _isIndexing = false;
        private static volatile string _currentStatus = "";

        private static dynamic _kb;
        private static bool _isOpenInProgress = false;
        private static readonly object _kbLock = new object();

        public KbService(IndexCacheService indexCacheService)
        {
            _indexCacheService = indexCacheService;
        }

        public void SetBuildService(BuildService bs) { _buildService = bs; }
        public IndexCacheService GetIndexCache() { return _indexCacheService; }
        public bool IsInitializing => _isOpenInProgress;
        public bool IsIndexing => _isIndexing;
        public bool IsOpen { get { lock (_kbLock) { return _kb != null; } } }

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
                        
                        var options = new KnowledgeBase.OpenOptions(path);
                        _kb = KnowledgeBase.Open(options);
                        
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

            _isIndexing = true;
            _processedCount = 0;
            _totalCount = 0;
            _currentStatus = "Scanning objects...";

            // Start indexing in a dedicated STA thread to prevent blocking the command consumer
            var indexThread = new Thread(() => {
                try {
                    dynamic kb = GetKB();
                    if (kb == null) { 
                        _isIndexing = false; 
                        _currentStatus = "Error: KB not open";
                        return; 
                    }
                    
                    _currentStatus = "Capturing KB objects snapshot...";
                    Logger.Info(_currentStatus);

                    var objectList = (System.Collections.IEnumerable)kb.DesignModel.Objects;
                    var objectSnapshot = new List<KeyValuePair<Guid, string>>();
                    foreach (global::Artech.Architecture.Common.Objects.KBObject obj in objectList)
                    {
                        objectSnapshot.Add(new KeyValuePair<Guid, string>(obj.Guid, obj.Name));
                        _totalCount++;
                        if (_totalCount % 500 == 0) Thread.Sleep(1); // Small breather during capture
                    }

                    _currentStatus = $"Indexing {_totalCount} objects using snapshot...";
                    Logger.Info(_currentStatus);

                    foreach (var snapshotEntry in objectSnapshot)
                    {
                        try {
                            // Fetch object safely by stable identity. Name-based dynamic dispatch
                            // can bind to the wrong GeneXus SDK overload during bulk indexing.
                            var obj = kb.DesignModel.Objects.Get(snapshotEntry.Key);
                            if (obj == null) continue;

                            _indexCacheService.UpdateEntry(obj);
                            _processedCount++;
                            
                            // ELITE: Adaptive notifications. 
                            int notifyInterval = Math.Max(500, _totalCount / 100);
                            if (_processedCount % notifyInterval == 0 || _processedCount == _totalCount) {
                                _currentStatus = $"Processed {_processedCount}/{_totalCount}";
                                Logger.Info(_currentStatus);
                                
                                Program.SendNotification("notifications/status", new {
                                    status = "Indexing",
                                    processed = _processedCount,
                                    total = _totalCount,
                                    message = _currentStatus
                                });
                            }
                            
                            // Ironclad Throttling: Give the system a breather every 50 objects
                            if (_processedCount % 50 == 0) Thread.Sleep(10);
                        } catch (Exception ex) {
                            Logger.Error($"Error indexing object {snapshotEntry.Value}: {ex.Message}");
                        }
                    }

                    _currentStatus = "Complete";
                    _isIndexing = false;
                    Logger.Info("BulkIndex completed successfully.");
                } catch (Exception ex) {
                    Logger.Error("BulkIndex FATAL: " + ex.Message);
                    _isIndexing = false;
                    _currentStatus = "Error: " + ex.Message;
                }
            }) { 
                IsBackground = true, 
                Name = "AsyncIndexer", 
                Priority = ThreadPriority.BelowNormal 
            };
            indexThread.SetApartmentState(ApartmentState.STA);
            indexThread.Start();

            return "{\"status\":\"Started\"}";
        }

        public string GetIndexStatus()
        {
            var json = new Newtonsoft.Json.Linq.JObject();
            json["isIndexing"] = _isIndexing;
            json["total"] = _totalCount;
            json["processed"] = _processedCount;
            json["status"] = _currentStatus;
            json["isBusy"] = _isIndexing || _isOpenInProgress;
            return json.ToString();
        }

        public string EnsureNotIndexing()
        {
            if (_isIndexing)
            {
                return "{\"error\": \"Knowledge Base is currently busy performing a background indexing task. Please wait a few seconds and try again.\", \"isBusy\": true}";
            }
            if (_isOpenInProgress)
            {
                return "{\"error\": \"Knowledge Base is currently opening. Please wait.\", \"isBusy\": true}";
            }
            return null;
        }
    }
}
