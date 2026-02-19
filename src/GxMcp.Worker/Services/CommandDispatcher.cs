using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class CommandDispatcher
    {
        private readonly BuildService _buildService;
        private readonly KbService _kbService;
        private readonly ObjectService _objectService;
        private readonly WriteService _writeService;
        private readonly ListService _listService;
        private readonly AnalyzeService _analyzeService;
        private readonly ForgeService _forgeService;
        private readonly RefactorService _refactorService;
        private readonly DoctorService _doctorService;
        private readonly SearchService _searchService;
        private readonly HistoryService _historyService;
        private readonly WikiService _wikiService;
        private readonly BatchService _batchService;
        private readonly VisualizerService _visualizerService;
        private readonly IndexCacheService _indexCacheService;

        public CommandDispatcher()
        {
            _buildService = new BuildService();
            _indexCacheService = new IndexCacheService();
            _kbService = new KbService(_buildService, _indexCacheService);
            _objectService = new ObjectService(_buildService, _kbService);
            _analyzeService = new AnalyzeService(_objectService, _indexCacheService);
            _writeService = new WriteService(_objectService, _buildService, _kbService, _analyzeService);
            _listService = new ListService(_buildService, _kbService, _indexCacheService);
            _forgeService = new ForgeService(_buildService, _objectService, _analyzeService);
            _refactorService = new RefactorService(_objectService, _buildService);
            _doctorService = new DoctorService();
            _searchService = new SearchService(_indexCacheService);
            _historyService = new HistoryService(_objectService, _writeService);
            _wikiService = new WikiService(_objectService);
            _batchService = new BatchService(_objectService, _buildService, _analyzeService);
            _visualizerService = new VisualizerService();
        }

        public string Dispatch(string jsonRpc)
        {
            try 
            {
                var request = JObject.Parse(jsonRpc);
                var prms = request["params"] as JObject;
                
                string module = prms?["module"]?.ToString();
                string action = prms?["action"]?.ToString();
                string target = prms?["target"]?.ToString();
                string payload = prms?["payload"]?.ToString();
                string part = prms?["part"]?.ToString();

                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                Console.Error.WriteLine($"[Worker] Executable: {exePath}");
                Console.Error.WriteLine($"[Worker] Dispatching: {module} / {action} / {target}");


                string debugLog = $"[Worker] Dispatching: Mod={module} Act={action} Tgt={target} Prt={part} Pay={payload}";
                Console.Error.WriteLine(debugLog);
                try { System.IO.File.AppendAllText("C:\\Projetos\\GenexusMCP\\worker_debug.log", debugLog + Environment.NewLine); } catch {}

                switch (module?.ToLower())
                {
                    case "build":
                    case "sync":
                    case "reorg":
                        return _buildService.Execute(action ?? module, target);

                    case "read":
                        if (string.Equals(action, "ExtractSource", StringComparison.OrdinalIgnoreCase)) return _objectService.ReadObjectSource(target, part);
                        if (string.Equals(action, "ReadSection", StringComparison.OrdinalIgnoreCase)) return _objectService.ReadObjectSection(target, part, payload);
                        if (string.Equals(action, "GetVariables", StringComparison.OrdinalIgnoreCase)) return _objectService.GetVariables(target);
                        if (string.Equals(action, "GetAttribute", StringComparison.OrdinalIgnoreCase)) return _objectService.GetAttributeMetadata(target);
                        return _objectService.ReadObject(target);

                    case "write":
                        if (string.Equals(action, "WriteSection", StringComparison.OrdinalIgnoreCase)) {
                            string sectionName = prms?["section"]?.ToString();
                            return _writeService.WriteObjectSection(target, part, sectionName, payload);
                        }
                        return _writeService.WriteObject(target, part ?? action, payload);

                    case "listobjects":
                        int limit = prms?["limit"]?.ToObject<int>() ?? 100;
                        int offset = prms?["offset"]?.ToObject<int>() ?? 0;
                        return _listService.ListObjects(target, limit, offset);

                    case "analyze":
                        if (string.Equals(action, "ListSections", StringComparison.OrdinalIgnoreCase)) return _analyzeService.ListSections(target, part);
                        if (string.Equals(action, "GetHierarchy", StringComparison.OrdinalIgnoreCase)) return _analyzeService.GetTransactionHierarchy(target);
                        return _analyzeService.Analyze(target);

                    case "forge":
                        return _forgeService.CreateObject(target, payload);

                    case "refactor":
                        return _refactorService.Refactor(target, action);

                    case "doctor":
                        return _doctorService.Diagnose(target);

                    case "search":
                        return _searchService.Search(target);

                    case "history":
                        return _historyService.Execute(target, action);

                    case "wiki":
                        return _wikiService.Generate(target);

                    case "batch":
                        return _batchService.Execute(target, action, payload);

                    case "visualize":
                        return _visualizerService.GenerateGraph(payload);

                    case "genexus":
                        if (action == "Test") return "{\"status\":\"Echo OK\"}";
                        if (action == "BulkIndex") return _kbService.BulkIndex();
                        if (action == "IndexPrefix") return _kbService.IndexPrefix(target);
                        break;
                }

                return "{\"error\":\"Unknown module: " + module + "\"}";
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Worker Dispatch Error] {ex.Message}");
                return "{\"error\":\"" + EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string GetId(string json)
        {
            try
            {
                var obj = JObject.Parse(json);
                return obj["id"]?.ToString();
            }
            catch
            {
                return null;
            }
        }

        public static string EscapeJsonString(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}
