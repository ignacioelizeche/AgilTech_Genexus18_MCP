using System;
using System.IO;
using GxMcp.Worker.Models;
using GxMcp.Worker.Helpers;
using System.Threading.Tasks;

namespace GxMcp.Worker.Services
{
    public class IndexCacheService
    {
        private SearchIndex _index;
        private string _indexPath;
        private readonly BuildService _buildService;
        private bool _initialized = false;
        private readonly object _lock = new object();
        private bool _savingInProgress = false;

        public IndexCacheService(BuildService buildService)
        {
            _buildService = buildService;
            _indexPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "search_index.json");
        }

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

        public SearchIndex GetIndex()
        {
            EnsureInitialized();
            if (_index != null) return _index;

            lock (_lock)
            {
                if (_index != null) return _index;
                try
                {
                    if (File.Exists(_indexPath))
                    {
                        string json = File.ReadAllText(_indexPath);
                        _index = SearchIndex.FromJson(json);
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
            _index = index;
            // Fire and forget save to disk
            Task.Run(() => FlushToDisk());
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
                    File.WriteAllText(_indexPath, _index.ToJson());
                    Logger.Debug("Index flushed to disk (Background)");
                }
                catch (Exception ex) { Logger.Error("Flush Error: " + ex.Message); }
                finally { _savingInProgress = false; }
            }
        }

        public void UpdateEntry(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            var index = GetIndex();
            var entry = new SearchIndex.IndexEntry
            {
                Name = obj.Name,
                Type = obj.TypeDescriptor.Name,
                Description = obj.Description
            };

            if (obj is global::Artech.Genexus.Common.Objects.Attribute attr)
            {
                entry.DataType = attr.Type.ToString();
                entry.Length = attr.Length;
                entry.Decimals = attr.Decimals;
            }

            string key = string.Format("{0}:{1}", entry.Type, entry.Name);
            lock (_lock)
            {
                if (index.Objects.ContainsKey(key)) index.Objects[key] = entry;
                else index.Objects.Add(key, entry);
            }
            UpdateIndex(index);
        }

        public void RemoveEntry(string type, string name)
        {
            var index = GetIndex();
            string key = string.Format("{0}:{1}", type, name);
            lock (_lock)
            {
                if (index.Objects.Remove(key)) UpdateIndex(index);
            }
        }

        public void Clear() { lock (_lock) { _index = null; } }
    }
}
