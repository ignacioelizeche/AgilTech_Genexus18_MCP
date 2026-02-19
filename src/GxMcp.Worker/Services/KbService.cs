using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class KbService
    {
        private static KnowledgeBase _kb;
        private readonly BuildService _buildService;
        private readonly IndexCacheService _indexCacheService;

        public KbService(BuildService buildService, IndexCacheService indexCacheService)
        {
            _buildService = buildService;
            _indexCacheService = indexCacheService;
        }

        public KnowledgeBase GetKB() { EnsureKbOpen(); return _kb; }

        public void Reload()
        {
            if (_kb != null) { try { _kb.Close(); } catch { } _kb = null; GC.Collect(); }
            EnsureKbOpen();
        }

        private void EnsureKbOpen()
        {
            if (_kb != null) return;
            string gxPath = @"C:\Program Files (x86)\GeneXus\GeneXus18";
            
            try {
                string kbPath = _buildService.GetKBPath();
                if (string.IsNullOrEmpty(kbPath)) throw new Exception("KBPath not configured in config.json");

                Logger.Info($"[KbService] Opening KB: {kbPath}");
                string oldDir = Directory.GetCurrentDirectory();
                try {
                    Directory.SetCurrentDirectory(gxPath);
                    _kb = KnowledgeBase.Open(new KnowledgeBase.OpenOptions(kbPath));
                } finally { Directory.SetCurrentDirectory(oldDir); }

                if (_kb == null) throw new Exception("KnowledgeBase.Open returned null.");
                Logger.Info("[KbService] KB Opened Successfully.");
            } catch (Exception ex) { 
                Logger.Error($"[KbService] SDK Error: {ex.Message}"); 
                throw new Exception($"Failed to connect to GeneXus KB: {ex.Message}");
            }
        }

        public string IndexPrefix(string prefix)
        {
            try
            {
                var kb = GetKB();
                var index = _indexCacheService.GetIndex() ?? new Models.SearchIndex();

                int count = 0;
                foreach (KBObject kbo in kb.DesignModel.Objects)
                {
                    try {
                        if (kbo == null || !kbo.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        string n = kbo.Name;
                        string t = GetPrefix(kbo);
                        string k = t + ":" + n;
                        index.Objects[k] = new Models.SearchIndex.IndexEntry {
                            Name = k, Type = t, Description = kbo.Description,
                            Tags = new List<string>{t}, Keywords = new List<string>{t, n},
                            Calls = new List<string>(), CalledBy = new List<string>(),
                            BusinessDomain = "Protocolo"
                        };
                        count++;
                    } catch { continue; }
                }
                index.LastUpdated = DateTime.Now;
                _indexCacheService.UpdateIndex(index);
                return "{\"status\":\"Success\", \"indexed\":" + count + "}";
            } catch (Exception ex) { return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}"; }
        }

        public string BulkIndex()
        {
            try {
                var kb = GetKB();
                var index = new Models.SearchIndex();
                var concurrentDict = new System.Collections.Concurrent.ConcurrentDictionary<string, Models.SearchIndex.IndexEntry>(StringComparer.OrdinalIgnoreCase);

                var objects = kb.DesignModel.Objects.Cast<KBObject>().ToList();
                System.Threading.Tasks.Parallel.ForEach(objects, kbo => {
                    try {
                        if (kbo == null) return;
                        string n = kbo.Name;
                        string t = GetPrefix(kbo);
                        string k = t + ":" + n;
                        
                        var entry = new Models.SearchIndex.IndexEntry {
                            Name = k, Type = t, Description = kbo.Description,
                            Tags = new List<string>{t}, Keywords = new List<string>{t, n},
                            Calls = new List<string>(), CalledBy = new List<string>()
                        };
                        concurrentDict[k] = entry;
                    } catch { }
                });

                index.Objects = concurrentDict.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
                index.LastUpdated = DateTime.Now;
                _indexCacheService.UpdateIndex(index);
                return "{\"status\":\"Success\", \"indexed\":" + index.Objects.Count + "}";
            } catch (Exception ex) { return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}"; }
        }

        private string GetPrefix(KBObject obj)
        {
            if (obj == null) return "Obj";
            string typeName = obj.TypeDescriptor.Name;

            switch (typeName.ToLower())
            {
                case "procedure": return "Prc";
                case "transaction": return "Trn";
                case "webpanel": return "Wbp";
                case "dataview": return "Dvw";
                case "dataprovider": return "Dpr";
                case "sdpanel": return "Sdp";
                case "menu": return "Mnu";
                case "attribute": return "Att";
                case "table": return "Tbl";
                case "domain": return "Dom";
                default: return typeName.Length > 3 ? typeName.Substring(0, 3) : typeName;
            }
        }
    }
}
