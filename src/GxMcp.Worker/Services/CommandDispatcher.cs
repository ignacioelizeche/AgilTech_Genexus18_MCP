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

        public CommandDispatcher()
        {
            _buildService = new BuildService();
            _kbService = new KbService(_buildService);
            _objectService = new ObjectService(_buildService, _kbService);
            _analyzeService = new AnalyzeService(_objectService);
            _writeService = new WriteService(_objectService, _buildService, _kbService, _analyzeService);
            _listService = new ListService(_buildService, _kbService);
            _forgeService = new ForgeService(_buildService, _objectService, _analyzeService);
            _refactorService = new RefactorService(_objectService, _buildService);
            _doctorService = new DoctorService();
            _searchService = new SearchService();
            _historyService = new HistoryService(_objectService, _writeService);
            _wikiService = new WikiService(_objectService);
            _batchService = new BatchService(_objectService, _buildService, _analyzeService);
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

                Console.Error.WriteLine($"[Worker] Dispatching: {module} / {action} / {target}");

                switch (module?.ToLower())
                {
                    case "build":
                    case "sync":
                    case "reorg":
                        return _buildService.Execute(action ?? module, target);

                    case "read":
                        return _objectService.ReadObject(target);

                    case "write":
                        return _writeService.WriteObject(target, part ?? action, payload);

                    case "listobjects":
                        int limit = prms?["limit"]?.ToObject<int>() ?? 100;
                        int offset = prms?["offset"]?.ToObject<int>() ?? 0;
                        return _listService.ListObjects(target, limit, offset);

                    case "analyze":
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

                    case "genexus":
                        if (action == "Test") return "{\"status\":\"Echo OK\"}";
                        if (action == "BulkIndex") return _kbService.BulkIndex();
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
