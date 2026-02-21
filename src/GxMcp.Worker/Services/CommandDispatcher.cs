using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class CommandDispatcher
    {
        private static CommandDispatcher _instance;
        private static readonly object _lock = new object();

        private readonly KbService _kbService;
        private readonly ObjectService _objectService;
        private readonly WriteService _writeService;
        private readonly ListService _listService;
        private readonly AnalyzeService _analyzeService;
        private readonly DataInsightService _dataInsightService;
        private readonly UIService _uiService;
        private readonly BuildService _buildService;
        private readonly RefactorService _refactorService;
        private readonly BatchService _batchService;
        private readonly ForgeService _forgeService;
        private readonly ValidationService _validationService;
        private readonly TestService _testService;
        private readonly IndexCacheService _indexCacheService;
        private readonly SearchService _searchService;
        private readonly WikiService _wikiService;
        private readonly HistoryService _historyService;
        private readonly VisualizerService _visualizerService;
        private readonly HealthService _healthService;
        private readonly LinterService _linterService;
        private readonly PatternService _patternService;
        private readonly PatchService _patchService;

        private CommandDispatcher()
        {
            _buildService = new BuildService();
            _indexCacheService = new IndexCacheService(_buildService);
            _kbService = new KbService(_buildService, _indexCacheService);
            _objectService = new ObjectService(_kbService, _buildService);
            _writeService = new WriteService(_objectService);
            _listService = new ListService(_kbService);
            _analyzeService = new AnalyzeService(_kbService, _objectService, _indexCacheService);
            _dataInsightService = new DataInsightService(_kbService, _objectService);
            _uiService = new UIService(_kbService, _objectService);
            _objectService.SetDataInsightService(_dataInsightService);
            _objectService.SetUIService(_uiService);
            _refactorService = new RefactorService(_kbService);
            _batchService = new BatchService(_kbService, _writeService);
            _forgeService = new ForgeService(_kbService);
            _validationService = new ValidationService(_kbService);
            _testService = new TestService(_kbService, _buildService);
            _searchService = new SearchService(_indexCacheService);
            _wikiService = new WikiService(_objectService);
            _historyService = new HistoryService(_objectService, _writeService);
            _visualizerService = new VisualizerService();
            _healthService = new HealthService();
            _linterService = new LinterService(_objectService);
            _patternService = new PatternService(_indexCacheService, _objectService);
            _patchService = new PatchService(_objectService, _writeService);
        }

        public static CommandDispatcher Instance
        {
            get { lock (_lock) { return _instance ?? (_instance = new CommandDispatcher()); } }
        }

        public string Dispatch(string line)
        {
            try
            {
                Logger.Debug(string.Format("Dispatching command: {0}", line.Length > 100 ? line.Substring(0, 100) : line));
                var request = JObject.Parse(line);
                string method = request["method"] != null ? request["method"].ToString() : null;
                var @params = request["params"] as JObject ?? new JObject();

                switch (method)
                {
                    case "genexus_list_objects":
                    case "genexus_search":
                        string q = @params["query"] != null ? @params["query"].ToString() : (@params["filter"] != null ? @params["filter"].ToString() : null);
                        return _searchService.Search(q, null, null, @params["limit"] != null ? (int)@params["limit"] : 100);
                    case "genexus_read_object":
                        return _objectService.ReadObject(@params["name"] != null ? @params["name"].ToString() : null);
                    case "genexus_read_source":
                        return _objectService.ReadObjectSource(
                            @params["name"] != null ? @params["name"].ToString() : null, 
                            @params["part"] != null ? @params["part"].ToString() : "Source",
                            @params["offset"] != null ? (int?)@params["offset"] : null,
                            @params["limit"] != null ? (int?)@params["limit"] : null
                        );
                    case "genexus_write_object":
                        return _writeService.WriteObject(@params["name"] != null ? @params["name"].ToString() : null, @params["part"] != null ? @params["part"].ToString() : null, @params["code"] != null ? @params["code"].ToString() : null);
                    case "genexus_analyze":
                        return _analyzeService.Analyze(@params["name"] != null ? @params["name"].ToString() : null);
                    case "genexus_build":
                        return _buildService.Build(@params["action"] != null ? @params["action"].ToString() : null, @params["target"] != null ? @params["target"].ToString() : null);
                    case "genexus_doctor":
                        return _buildService.Doctor(@params["logPath"] != null ? @params["logPath"].ToString() : null);
                    case "genexus_batch":
                        return _batchService.ProcessBatch(@params["action"] != null ? @params["action"].ToString() : null, @params["name"] != null ? @params["name"].ToString() : null, @params["code"] != null ? @params["code"].ToString() : null);
                    case "genexus_refactor":
                        return _refactorService.Refactor(@params["name"] != null ? @params["name"].ToString() : null, @params["action"] != null ? @params["action"].ToString() : null);
                    case "genexus_validate":
                        return _validationService.ValidateCode(@params["name"] != null ? @params["name"].ToString() : null, @params["part"] != null ? @params["part"].ToString() : null, @params["code"] != null ? @params["code"].ToString() : null);
                    case "genexus_test":
                        return _testService.RunTest(@params["name"] != null ? @params["name"].ToString() : null);
                    case "genexus_scaffold":
                        return _forgeService.Scaffold(@params["type"] != null ? @params["type"].ToString() : null, @params["name"] != null ? @params["name"].ToString() : null, @params);
                    case "genexus_patch":
                        return _patchService.ApplyPatch(
                            @params["name"] != null ? @params["name"].ToString() : null, 
                            @params["part"] != null ? @params["part"].ToString() : "Source", 
                            @params["operation"] != null ? @params["operation"].ToString() : null, 
                            @params["content"] != null ? @params["content"].ToString() : null, 
                            @params["context"] != null ? @params["context"].ToString() : null,
                            @params["expectedCount"] != null ? (int)@params["expectedCount"] : 1
                        );
                    case "genexus_bulk_index":
                        return _kbService.BulkIndex();
                    case "genexus_get_attribute":
                        return _analyzeService.GetAttributeMetadata(@params["name"] != null ? @params["name"].ToString() : null);
                    case "genexus_get_hierarchy":
                        return _analyzeService.GetHierarchy(@params["name"] != null ? @params["name"].ToString() : null);
                    case "genexus_get_variables":
                        return _analyzeService.GetVariables(@params["name"] != null ? @params["name"].ToString() : null);
                    case "genexus_get_data_context":
                        return _dataInsightService.GetDataContext(@params["name"] != null ? @params["name"].ToString() : null);
                    case "genexus_get_ui_context":
                        return _uiService.GetUIContext(@params["name"] != null ? @params["name"].ToString() : null);
                    case "genexus_wiki":
                        return _wikiService.Generate(@params["name"] != null ? @params["name"].ToString() : null);
                    case "genexus_visualize":
                        return _visualizerService.GenerateGraph(@params["domain"] != null ? @params["domain"].ToString() : null);
                    case "genexus_linter":
                        return _linterService.Lint(@params["name"] != null ? @params["name"].ToString() : null);
                    case "genexus_health_report":
                        return _healthService.GetHealthReport();
                    case "genexus_history":
                        return _historyService.Execute(@params["name"] != null ? @params["name"].ToString() : null, @params["action"] != null ? @params["action"].ToString() : null);
                }

                if (method == "execute_command")
                {
                    string module = @params["module"] != null ? @params["module"].ToString() : null;
                    string action = @params["action"] != null ? @params["action"].ToString() : null;
                    string target = @params["target"] != null ? @params["target"].ToString() : null;
                    string payload = @params["payload"] != null ? @params["payload"].ToString() : null;

                    switch (module)
                    {
                        case "KB":
                            if (action == "BulkIndex") return _kbService.BulkIndex();
                            return "{\"error\": \"Action not found in KB\"}";
                        case "ListObjects":
                            return _searchService.Search(null, target, null, @params["limit"] != null ? (int)@params["limit"] : 100);
                        case "Search":
                            return _searchService.Search(target);
                        case "Read":
                            if (action == "ExtractSource") return _objectService.ReadObjectSource(target, @params["part"] != null ? @params["part"].ToString() : "Source");
                            if (action == "GetAttribute") return _analyzeService.GetAttributeMetadata(target);
                            if (action == "GetVariables") return _analyzeService.GetVariables(target);
                            return _objectService.ReadObject(target);
                        case "Write":
                            return _writeService.WriteObject(target, action, payload);
                        case "Analyze":
                            if (action == "GetHierarchy") return _analyzeService.GetHierarchy(target);
                            if (action == "GetDataContext") return _dataInsightService.GetDataContext(target);
                            return _analyzeService.Analyze(target);
                        case "UI":
                            if (action == "GetUIContext") return _uiService.GetUIContext(target);
                            return "{\"error\":\"Unknown UI action\"}";
                        case "Doctor": return _buildService.Doctor(target);
                        case "Batch": return _batchService.ProcessBatch(action, target, payload);
                        case "Forge": return _forgeService.Scaffold(target, payload, @params);
                        case "Refactor": return _refactorService.Refactor(target, action);
                        case "Wiki": return _wikiService.Generate(target);
                        case "History": return _historyService.Execute(target, action);
                        case "Visualizer": return _visualizerService.GenerateGraph(target);
                        case "Linter": return _linterService.Lint(target);
                        case "Health": return _healthService.GetHealthReport();
                        case "Pattern": return _patternService.GetSample(target);
                        case "Patch": return _patchService.ApplyPatch(
                            target, 
                            @params["part"] != null ? @params["part"].ToString() : null, 
                            @params["operation"] != null ? @params["operation"].ToString() : null, 
                            @params["content"] != null ? @params["content"].ToString() : null, 
                            @params["context"] != null ? @params["context"].ToString() : null,
                            @params["expectedCount"] != null ? (int)@params["expectedCount"] : 1
                        );
                        case "Test": return _testService.RunTest(target);
                        case "Validation": return _validationService.ValidateCode(target, @params["part"] != null ? @params["part"].ToString() : null, payload);
                    }
                }

                return "{\"error\": \"Method not found: " + EscapeJsonString(method) + "\"}";
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public static string EscapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}
