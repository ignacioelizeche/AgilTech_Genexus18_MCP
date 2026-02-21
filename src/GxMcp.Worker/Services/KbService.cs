using System;
using System.IO;
using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using System.Reflection;
using System.Linq;

namespace GxMcp.Worker.Services
{
    public class KbService
    {
        private static global::Artech.Architecture.Common.Objects.KnowledgeBase _kb;
        private static readonly object _kbLock = new object();
        private readonly BuildService _buildService;
        private readonly IndexCacheService _indexCacheService;

        public KbService(BuildService buildService, IndexCacheService indexCacheService)
        {
            _buildService = buildService;
            _indexCacheService = indexCacheService;
        }

        public global::Artech.Architecture.Common.Objects.KnowledgeBase GetKB() 
        { 
            if (_kb != null) return _kb;
            EnsureKbOpen(); 
            return _kb; 
        }

        private void EnsureKbOpen()
        {
            if (_kb != null) return;
            lock (_kbLock)
            {
                if (_kb != null) return;
                
                string gxPath = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR") ?? @"C:\Program Files (x86)\GeneXus\GeneXus18";
                try {
                    string kbPath = _buildService.GetKBPath();
                    if (kbPath.EndsWith(".gxw", StringComparison.OrdinalIgnoreCase)) kbPath = Path.GetDirectoryName(kbPath);

                    // Initialize the cache path based on the actual KB being opened
                    _indexCacheService.Initialize(kbPath);

                    string oldDir = Directory.GetCurrentDirectory();
                    try {
                        Directory.SetCurrentDirectory(gxPath);
                        Logger.Info($"Opening KB in OFFLINE mode: {kbPath}");

                        var options = new global::Artech.Architecture.Common.Objects.KnowledgeBase.OpenOptions(kbPath);
                        options.EnableMultiUser = true;
                        options.AvoidIndexing = true;
                        
                        // GX18 Hardening: Try to disable all background checks
                        try {
                            var props = options.GetType().GetProperties();
                            foreach(var p in props) {
                                if (p.Name == "NoAutomaticUpdate" || p.Name == "OfflineMode") {
                                    p.SetValue(options, true);
                                    Logger.Debug($"Option {p.Name} set to true.");
                                }
                            }
                        } catch { }

                        _kb = global::Artech.Architecture.Common.Objects.KnowledgeBase.Open(options);
                        if (_kb == null) throw new Exception("KnowledgeBase.Open returned null");
                        
                        Logger.Info($"KB opened successfully. DesignModel: {_kb.DesignModel?.Name}");
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
            try
            {
                var kb = GetKB();
                if (kb == null) return "{\"error\":\"KB not open\"}";
                
                Logger.Info("Bulk Indexing starting (Parallel)...");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                
                var index = new SearchIndex { LastUpdated = DateTime.Now };
                var concurrentDict = new System.Collections.Concurrent.ConcurrentDictionary<string, SearchIndex.IndexEntry>();
                
                var objects = kb.DesignModel.Objects.GetAll().ToList();
                int total = objects.Count;
                int processed = 0;

                System.Threading.Tasks.Parallel.ForEach(objects, (obj) => {
                    var entry = new SearchIndex.IndexEntry {
                        Name = obj.Name,
                        Type = obj.TypeDescriptor.Name,
                        Description = obj.Description
                    };
                    concurrentDict.TryAdd($"{entry.Type}:{entry.Name}", entry);
                    
                    var current = System.Threading.Interlocked.Increment(ref processed);
                    if (current % 1000 == 0) {
                        Logger.Info($"Progress: {current}/{total} objects indexed...");
                    }
                });

                foreach (var kvp in concurrentDict) index.Objects[kvp.Key] = kvp.Value;

                _indexCacheService.UpdateIndex(index);
                sw.Stop();
                
                Logger.Info($"Bulk Indexing complete. {total} objects in {sw.ElapsedMilliseconds}ms.");
                return "{\"status\":\"Success\", \"totalIndexed\":" + total + ", \"timeMs\":" + sw.ElapsedMilliseconds + "}";
            } catch (Exception ex) { 
                Logger.Error($"BulkIndex Fatal: {ex.Message}");
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}"; 
            }
        }
    }
}
