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

        private static CommandDispatcher _instance;
        public static CommandDispatcher Instance => _instance ?? (_instance = new CommandDispatcher());

        public CommandDispatcher()
        {
            Console.Error.WriteLine("[Worker] Initializing persistent services...");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            _buildService = new BuildService();
            _indexCacheService = new IndexCacheService();
            _kbService = new KbService(_buildService, _indexCacheService);
            _buildService.SetKbService(_kbService); // Circular dependency handled via setter
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

            sw.Stop();
            Console.Error.WriteLine($"[Worker] Services initialized in {sw.ElapsedMilliseconds}ms");
        }

        public void PreWarm()
        {
            try {
                Console.Error.WriteLine("[Worker] Pre-warming KB connection...");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _kbService.GetKB();
                sw.Stop();
                Console.Error.WriteLine($"[Worker] KB pre-warmed in {sw.ElapsedMilliseconds}ms");
            } catch (Exception ex) {
                Console.Error.WriteLine($"[Worker] Pre-warm failed: {ex.Message}");
            }
        }

        public string Dispatch(string jsonRpc)
        {
            var totalSw = System.Diagnostics.Stopwatch.StartNew();
            try 
            {
                var request = JObject.Parse(jsonRpc);
                var prms = request["params"] as JObject;
                
                string module = prms?["module"]?.ToString();
                string action = prms?["action"]?.ToString();
                string target = prms?["target"]?.ToString();
                string payload = prms?["payload"]?.ToString();
                string part = prms?["part"]?.ToString();

                Console.Error.WriteLine($"[Worker] Dispatching: {module} / {action} / {target}");

                string result = "";
                switch (module?.ToLower())
                {
                    case "build":
                    case "sync":
                    case "reorg":
                        result = _buildService.Execute(action ?? module, target);
                        break;

                    case "read":
                        if (string.Equals(action, "ExtractSource", StringComparison.OrdinalIgnoreCase)) result = _objectService.ReadObjectSource(target, part);
                        else if (string.Equals(action, "ReadSection", StringComparison.OrdinalIgnoreCase)) result = _objectService.ReadObjectSection(target, part, payload);
                        else if (string.Equals(action, "GetVariables", StringComparison.OrdinalIgnoreCase)) result = _objectService.GetVariables(target);
                        else if (string.Equals(action, "GetAttribute", StringComparison.OrdinalIgnoreCase)) result = _objectService.GetAttributeMetadata(target);
                        else result = _objectService.ReadObject(target);
                        break;

                    case "write":
                        if (string.Equals(action, "WriteSection", StringComparison.OrdinalIgnoreCase)) {
                            string sectionName = prms?["section"]?.ToString();
                            result = _writeService.WriteObjectSection(target, part, sectionName, payload);
                        }
                        else result = _writeService.WriteObject(target, part ?? action, payload);
                        break;

                    case "listobjects":
                        int limit = prms?["limit"]?.ToObject<int>() ?? 100;
                        int offset = prms?["offset"]?.ToObject<int>() ?? 0;
                        result = _listService.ListObjects(target, limit, offset);
                        break;

                    case "analyze":
                        if (string.Equals(action, "ListSections", StringComparison.OrdinalIgnoreCase)) result = _analyzeService.ListSections(target, part);
                        else if (string.Equals(action, "GetHierarchy", StringComparison.OrdinalIgnoreCase)) result = _analyzeService.GetTransactionHierarchy(target);
                        else result = _analyzeService.Analyze(target);
                        break;

                    case "forge":
                        result = _forgeService.CreateObject(target, payload);
                        break;

                    case "refactor":
                        result = _refactorService.Refactor(target, action);
                        break;

                    case "doctor":
                        result = _doctorService.Diagnose(target);
                        break;

                    case "search":
                        result = _searchService.Search(target);
                        break;

                    case "history":
                        result = _historyService.Execute(target, action);
                        break;

                    case "wiki":
                        result = _wikiService.Generate(target);
                        break;

                    case "batch":
                        result = _batchService.Execute(target, action, payload);
                        break;

                    case "visualize":
                        result = _visualizerService.GenerateGraph(payload);
                        break;

                    case "genexus":
                        if (action == "Test") result = "{\"status\":\"Echo OK\"}";
                        else if (action == "BulkIndex") result = _kbService.BulkIndex();
                        else if (action == "IndexPrefix") result = _kbService.IndexPrefix(target);
                        break;
                    
                    default:
                        result = "{\"error\":\"Unknown module: " + module + "\"}";
                        break;
                }

                totalSw.Stop();
                Console.Error.WriteLine($"[Worker] Command {module}/{action} completed in {totalSw.ElapsedMilliseconds}ms");
                return result;
            }
            catch (Exception ex)
            {
                totalSw.Stop();
                Console.Error.WriteLine($"[Worker Dispatch Error] {ex.Message} (after {totalSw.ElapsedMilliseconds}ms)");
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
