using System;
using System.IO;
using System.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class KbService
    {
        private static KnowledgeBase _kb;
        private readonly BuildService _buildService;

        public KbService(BuildService buildService)
        {
            _buildService = buildService;
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
        }

        private System.Reflection.Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            string assemblyName = new System.Reflection.AssemblyName(args.Name).Name;
            string dllName = assemblyName + ".dll";
            string gxPath = @"C:\Program Files (x86)\GeneXus\GeneXus18";
            
            string[] searchPaths = {
                gxPath,
                Path.Combine(gxPath, "Packages"),
                Path.Combine(gxPath, "Packages", "Patterns"),
                AppDomain.CurrentDomain.BaseDirectory
            };

            foreach (var path in searchPaths)
            {
                string fullPath = Path.Combine(path, dllName);
                if (File.Exists(fullPath))
                {
                    try 
                    {
                        return System.Reflection.Assembly.LoadFrom(fullPath);
                    }
                    catch { }
                }
            }

            return null;
        }

        public KnowledgeBase GetKB()
        {
            EnsureKbOpen();
            return _kb;
        }

        public void Reload()
        {
            if (_kb != null)
            {
                try 
                {
                    _kb.Close();
                }
                catch (Exception ex) 
                {
                    Logger.Error($"[KbService] Error closing KB during reload: {ex.Message}");
                }
                _kb = null;
                GC.Collect(); // Force cleanup
                Logger.Info("[KbService] KB Closed and Reload triggered.");
            }
            EnsureKbOpen();
        }

        private void EnsureKbOpen()
        {
            if (_kb != null) return;

            string kbPath = "";
            string gxPath = @"C:\Program Files (x86)\GeneXus\GeneXus18";

            try 
            {
                kbPath = _buildService.GetKBPath();
                if (string.IsNullOrEmpty(kbPath)) 
                    throw new Exception("KB Path is NULL or EMPTY.");

                if (!Directory.Exists(kbPath))
                    throw new Exception($"KB Directory DOES NOT EXIST: {kbPath}");

                string oldDir = Directory.GetCurrentDirectory();
                try 
                {
                    Logger.Info($"Setting CurrentDirectory to: {gxPath}");
                    Directory.SetCurrentDirectory(gxPath);

                    Logger.Info("Bootstrapping GeneXus SDK via Reflection...");

                    // 1. Set Disable UI
                    try 
                    {
                        string uiAsmPath = Path.Combine(gxPath, "Artech.Architecture.UI.Framework.dll");
                        if (File.Exists(uiAsmPath))
                        {
                            var uiAsm = System.Reflection.Assembly.LoadFrom(uiAsmPath);
                            var uiServicesType = uiAsm.GetType("Artech.Architecture.UI.Framework.Services.UIServices");
                            if (uiServicesType != null)
                            {
                                var setDisableMethod = uiServicesType.GetMethod("SetDisableUI", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                                if (setDisableMethod != null)
                                {
                                    setDisableMethod.Invoke(null, new object[] { true });
                                    Logger.Info("UIServices.SetDisableUI(true) OK.");
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Logger.Debug($"UIServices Bootstrap failed: {ex.Message}"); }

                    // 2. Initialize Core Services (Multi-Candidate)
                    string[] candidates = {
                        "Connector.dll|Artech.Core.Connector",
                        "Artech.Architecture.Common.dll|Artech.Architecture.Common.Services.ArtechServices",
                        "Artech.Architecture.Common.dll|Artech.Architecture.Common.Services.ContextService",
                        "Artech.Genexus.Common.dll|Artech.Genexus.Common.Services.CommonServices"
                    };

                    foreach (var candidate in candidates)
                    {
                        var parts = candidate.Split('|');
                        string dllName = parts[0];
                        string typeName = parts[1];
                        string dllPath = Path.Combine(gxPath, dllName);

                        if (!File.Exists(dllPath)) continue;

                        try 
                        {
                            var asm = System.Reflection.Assembly.LoadFrom(dllPath);
                            var type = asm.GetType(typeName);
                            if (type == null) continue;

                            // Try Initialize
                            var initMethod = type.GetMethod("Initialize", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                            if (initMethod != null)
                            {
                                initMethod.Invoke(null, null);
                                Logger.Info($"{typeName}.Initialize OK.");
                            }

                            // Special case for Connector: StartBL
                            if (typeName == "Artech.Core.Connector")
                            {
                                var startBLMethod = type.GetMethod("StartBL", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new Type[] { }, null);
                                if (startBLMethod != null)
                                {
                                    startBLMethod.Invoke(null, null);
                                    Logger.Info($"{typeName}.StartBL OK.");
                                }
                            }
                        }
                        catch (Exception ex) 
                        { 
                            Logger.Debug($"Candidate {candidate} initialization failed: {ex.Message}"); 
                        }
                    }

                    Logger.Info($"Attempting KnowledgeBase.Open: '{kbPath}'");
                    var options = new KnowledgeBase.OpenOptions(kbPath);
                    _kb = KnowledgeBase.Open(options);
                    
                    if (_kb != null)
                        Logger.Info($"KB Opened Successfully: {_kb.Name}");
                    else
                        Logger.Error("KnowledgeBase.Open returned NULL without exception.");
                }
                finally
                {
                    Directory.SetCurrentDirectory(oldDir);
                }
                
                if (_kb == null)
                    throw new Exception("KnowledgeBase.Open failed to return a valid KB instance.");
            }
            catch (Exception ex)
            {
                Logger.Error($"[KbService] SDK Critical Error: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }

        public string BulkIndex()
        {
            try
            {
                var kb = GetKB();
                string indexPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "search_index.json");
                var index = new Models.SearchIndex();
                string dir = Path.GetDirectoryName(indexPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                int count = 0;
                var objects = kb.DesignModel.Objects;
                foreach (KBObject kbo in objects)
                {
                    string prefix = GetPrefix(kbo);
                    string key = string.IsNullOrEmpty(prefix) ? kbo.Name : prefix + ":" + kbo.Name;

                    var entry = new Models.SearchIndex.IndexEntry
                    {
                        Name = key,
                        Type = prefix,
                        Description = kbo.Description,
                        Tags = new System.Collections.Generic.List<string> { kbo.GetType().Name },
                        Keywords = key.Replace(":", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList()
                    };
                    
                    if (kbo is Artech.Genexus.Common.Objects.Procedure) entry.Tags.Add("Logic-Engine");

                    index.Objects[key] = entry;
                    count++;
                }

                index.LastUpdated = DateTime.Now;
                File.WriteAllText(indexPath, index.ToJson());

                return "{\"status\":\"Success\", \"indexed\":" + count + "}";
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string GetPrefix(KBObject obj)
        {
            if (obj is Artech.Genexus.Common.Objects.Transaction) return "Trn";
            if (obj is Artech.Genexus.Common.Objects.Procedure) return "Prc";
            if (obj is Artech.Genexus.Common.Objects.WebPanel) return "Wbp";
            if (obj is Artech.Genexus.Common.Objects.Attribute) return "Att";
            if (obj is Artech.Genexus.Common.Objects.Table) return "Tbl";
            if (obj is Artech.Genexus.Common.Objects.Domain) return "Dom";
            return obj.GetType().Name;
        }
    }
}
