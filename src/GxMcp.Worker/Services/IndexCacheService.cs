using System;
using System.IO;
using GxMcp.Worker.Models;
using GxMcp.Worker.Helpers;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Concurrent;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Services.Structure;

namespace GxMcp.Worker.Services
{
    public class IndexCacheService
    {
        private SearchIndex _index;
        private string _indexPath;
        private BuildService _buildService;
        private bool _initialized = false;
        private readonly object _lock = new object();
        private string _lastSavedJsonHash = null;
        private DateTime _lastFlushTime = DateTime.MinValue;
        private bool _savingInProgress = false;
        private readonly VectorService _vectorService = new VectorService();

        public IndexCacheService()
        {
            _indexPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "search_index.json");
        }

        public void SetBuildService(BuildService bs) { _buildService = bs; }
        public KbService KbService => _buildService?.KbService;

        private void EnsureInitialized()
        {
            if (_initialized) return;
            try
            {
                string kbPath = _buildService.GetKBPath();
                if (!string.IsNullOrEmpty(kbPath)) Initialize(kbPath);
            }
            catch { }
        }

        public void Initialize(string kbPath)
        {
            if (string.IsNullOrEmpty(kbPath)) return;
            if (kbPath.EndsWith(".gxw", StringComparison.OrdinalIgnoreCase)) kbPath = Path.GetDirectoryName(kbPath);

            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string cacheDir = Path.Combine(localAppData, "GxMcp", "Cache");
                if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

                string hash = GetHash(kbPath);
                _indexPath = Path.Combine(cacheDir, string.Format("index_{0}.json", hash));
                _initialized = true;
                Logger.Info(string.Format("IndexCache initialized: {0}", _indexPath));

                // PERFORMANCE: Pro-active loading in background
                Task.Run(() => GetIndex());
            }
            catch (Exception ex) { Logger.Error("IndexCache Init Error: " + ex.Message); }
        }

        private string GetHash(string input)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input.ToLower().Trim()));
                return BitConverter.ToString(bytes).Replace("-", "").Substring(0, 16);
            }
        }

        private void BuildParentIndex(SearchIndex index)
        {
            var byParent = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.List<SearchIndex.IndexEntry>>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var entry in index.Objects.Values)
            {
                string parent = entry.Parent ?? "";
                if (!byParent.TryGetValue(parent, out var list))
                {
                    list = new System.Collections.Generic.List<SearchIndex.IndexEntry>();
                    byParent[parent] = list;
                }
                
                // PERFORMANCE: Since we are iterating dictionary values, there are NO duplicates. 
                // Using .Add() directly changes this from an O(N^2) operation to O(N).
                list.Add(entry);
            }
            index.ChildrenByParent = byParent;
        }

        private void AddOrUpdateEntryInParentIndex(System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.List<SearchIndex.IndexEntry>> byParent, SearchIndex.IndexEntry entry)
        {
            string parent = entry.Parent ?? "";
            var list = byParent.GetOrAdd(parent, _ => new System.Collections.Generic.List<SearchIndex.IndexEntry>());
            
            lock (list)
            {
                if (!list.Any(e => e.Name == entry.Name && e.Type == entry.Type))
                {
                    list.Add(entry);
                }
            }
        }

        public SearchIndex GetIndex()
        {
            if (_index != null) return _index;
            EnsureInitialized();

            lock (_lock)
            {
                if (_index != null) return _index;
                try
                {
                    if (File.Exists(_indexPath))
                    {
                        Logger.Debug(string.Format("Loading index from disk: {0}", _indexPath));
                        string json = File.ReadAllText(_indexPath);
                        _index = SearchIndex.FromJson(json);
                        BuildParentIndex(_index);
                        Logger.Info(string.Format("Index loaded. Objects: {0}", _index.Objects.Count));
                    }
                }
                catch (Exception ex) { Logger.Error("Load Index Error: " + ex.Message); }

                if (_index == null) _index = new SearchIndex();
                return _index;
            }
        }

        public void UpdateIndex(SearchIndex index)
        {
            lock (_lock)
            {
                BuildParentIndex(index);
                _index = index;
            }
            // Fire and forget save to disk with throttling
            if ((DateTime.Now - _lastFlushTime).TotalSeconds > 5)
            {
                Task.Run(() => FlushToDisk());
            }
        }

        private void FlushToDisk()
        {
            if (_savingInProgress) return;
            lock (_lock)
            {
                _savingInProgress = true;
                try
                {
                    string dir = Path.GetDirectoryName(_indexPath);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    
                    // PERFORMANCE: Use optimized JSON settings for faster serialization
                    var settings = new Newtonsoft.Json.JsonSerializerSettings { 
                        NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
                        DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore
                    };
                    string json = Newtonsoft.Json.JsonConvert.SerializeObject(_index, settings);
                    
                    File.WriteAllText(_indexPath, json);
                    _lastSavedJsonHash = GetJsonHash(json);
                    Logger.Debug("Index flushed to disk (Background)");
                }
                catch (Exception ex) { Logger.Error("Flush Error: " + ex.Message); }
                finally { 
                    _savingInProgress = false; 
                    _lastFlushTime = DateTime.Now;
                }
            }
        }

        private string GetJsonHash(string json)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(json));
                return BitConverter.ToString(bytes).Replace("-", "");
            }
        }

        public void UpdateEntry(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            var index = GetIndex();
            string parentName = null;
            string moduleName = null;
            try {
                if (obj.Parent != null && obj.Parent.Guid != obj.Guid) {
                    if (obj.Parent.TypeDescriptor.Name == "DesignModel") parentName = "Root Module";
                    else if (obj.Parent is global::Artech.Architecture.Common.Objects.Module || obj.Parent is global::Artech.Architecture.Common.Objects.Folder)
                        parentName = obj.Parent.Name;
                }
            } catch { }
            try { if (obj.Module != null && obj.Module.Guid != obj.Guid) moduleName = obj.Module.Name; } catch { }

            var entry = new SearchIndex.IndexEntry
            {
                Guid = obj.Guid.ToString(),
                Name = obj.Name,
                Type = obj.TypeDescriptor.Name,
                Description = obj.Description,
                Parent = parentName,
                Module = moduleName
            };

            if (obj is global::Artech.Genexus.Common.Objects.Attribute attr)
            {
                entry.DataType = attr.Type.ToString();
                entry.Length = attr.Length;
                entry.Decimals = attr.Decimals;
                entry.IsFormula = attr.Formula != null;
            }
            else if (obj is global::Artech.Genexus.Common.Objects.Table tbl)
            {
                entry.RootTable = tbl.Name;
                try {
                    var children = new Newtonsoft.Json.Linq.JArray();
                    dynamic dStructure = ((dynamic)tbl).TableStructure;
                    if (dStructure != null && dStructure.Attributes != null) {
                        foreach (dynamic tableAttr in dStructure.Attributes) 
                            children.Add(VisualStructureMapper.MapAttribute(tableAttr));
                    }
                    entry.SourceSnippet = children.ToString(Newtonsoft.Json.Formatting.None);
                } catch { }
            }
            else if (obj is global::Artech.Genexus.Common.Objects.Transaction trn)
            {
                entry.RootTable = trn.Structure.Root.Name;
                try { entry.ParmRule = trn.Rules.Source.Split('\n').FirstOrDefault(l => l.Trim().StartsWith("parm(", StringComparison.OrdinalIgnoreCase)); } catch { }
            }
            else if (obj is global::Artech.Genexus.Common.Objects.SDT sdt)
            {
                try {
                    entry.SourceSnippet = StructureParser.SerializeToText(obj);
                } catch (Exception ex) {
                    Logger.Error($"SDT index snippet failed for {obj.Name}: {ex.Message}");
                }
            }

            string key = string.Format("{0}:{1}", entry.Type, entry.Name);
            
            // Compute Embedding
            string semanticText = $"{entry.Name} {entry.Type} {entry.Description} {entry.RootTable} {entry.ParmRule}";
            entry.Embedding = _vectorService.ComputeEmbedding(semanticText);

            // Atomic update using ConcurrentDictionary
            index.Objects.AddOrUpdate(key, entry, (k, existing) => entry);

            if (index.ChildrenByParent != null)
            {
                AddOrUpdateEntryInParentIndex(index.ChildrenByParent, entry);
            }

            // ENRICHMENT: Dependencies (Calls and Tables) - ASYNCHRONOUS BACKGROUND
            Guid objGuid = obj.Guid;
            Program.BackgroundQueue.Enqueue(() => {
                try {
                    var kb = _buildService.KbService?.GetKB();
                    if (kb == null) return;
                    var bgObj = kb.DesignModel.Objects.Get(objGuid);
                    if (bgObj == null) return;

                    var references = bgObj.GetReferences();
                    if (references != null)
                    {
                        bool changed = false;
                        foreach (dynamic reference in references)
                        {
                            try
                            {
                                dynamic targetKey = reference.To;
                                if (targetKey == null) continue;
                                string targetName = targetKey.Name;
                                if (string.IsNullOrEmpty(targetName)) {
                                    string keyStr = targetKey.ToString();
                                    targetName = keyStr.Contains(":") ? keyStr.Split(':')[1] : keyStr;
                                }
                                if (string.IsNullOrEmpty(targetName)) continue;

                                string targetType = targetKey.TypeDescriptor.Name;
                                if (targetType == "Attribute" || targetType == "Table") {
                                    if (!entry.Tables.Contains(targetName)) { entry.Tables.Add(targetName); changed = true; }
                                } else {
                                    if (!entry.Calls.Contains(targetName)) { entry.Calls.Add(targetName); changed = true; }
                                }
                            } catch { }
                        }
                        if (changed) Task.Run(() => FlushToDisk());
                    }
                } catch (Exception ex) { Logger.Error("Background Indexing Error: " + ex.Message); }
            });
            
            // Fire and forget save to disk
            Task.Run(() => FlushToDisk());
        }

        public void RemoveEntry(string type, string name)
        {
            var index = GetIndex();
            string key = string.Format("{0}:{1}", type, name);
            lock (_lock)
            {
                if (index.Objects.TryRemove(key, out _)) UpdateIndex(index);
            }
        }

        public void Clear() { lock (_lock) { _index = null; } }
    }
}
