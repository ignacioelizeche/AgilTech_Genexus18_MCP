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
        private readonly IndexCacheService _indexCacheService;
        private readonly BuildService _buildService;
        private readonly WriteService _writeService;
        private readonly UIService _uiService;
        private readonly AnalyzeService _analyzeService;
        private readonly RefactorService _refactorService;
        private readonly BatchService _batchService;
        private readonly ForgeService _forgeService;
        private readonly ValidationService _validationService;
        private readonly TestService _testService;
        private readonly SearchService _searchService;
        private readonly WikiService _wikiService;
        private readonly HistoryService _historyService;
        private readonly VisualizerService _visualizerService;
        private readonly HealthService _healthService;
        private readonly NavigationService _navigationService;
        private readonly LinterService _linterService;
        private readonly PatternService _patternService;
        private readonly PatchService _patchService;
        private readonly SDTService _sdtService;
        private readonly StructureService _structureService;
        private readonly FormatService _formatService;
        private readonly PropertyService _propertyService;
        private readonly VersionControlService _versionControlService;
        private readonly ConversionService _conversionService;
        private readonly SelfTestService _selfTestService;
        private readonly PatternAnalysisService _patternAnalysisService;
        private readonly DataInsightService _dataInsightService;
        private readonly SummarizeService _summarizeService;
        private readonly InjectionService _injectionService;

        private CommandDispatcher()
        {
            // Phase 1: Creation
            _indexCacheService = new IndexCacheService();
            _buildService = new BuildService();
            _kbService = new KbService(_indexCacheService);
            _visualizerService = new VisualizerService();
            _healthService = new HealthService();
            _formatService = new FormatService();
            _objectService = new ObjectService(_kbService, _buildService);
            _navigationService = new NavigationService(_kbService);
            _summarizeService = new SummarizeService(_kbService, _objectService);
            _injectionService = new InjectionService(_kbService, _objectService, _analyzeService);
            _patternAnalysisService = new PatternAnalysisService(_objectService);
            _validationService = new ValidationService(_kbService);
            _searchService = new SearchService(_indexCacheService);
            _versionControlService = new VersionControlService(_kbService);
            _dataInsightService = new DataInsightService(_kbService, _objectService, _navigationService, _patternAnalysisService);
            _uiService = new UIService(_kbService, _objectService);
            _analyzeService = new AnalyzeService(_kbService, _objectService, _indexCacheService, _uiService);
            _writeService = new WriteService(_objectService);
            _refactorService = new RefactorService(_kbService, _objectService, _indexCacheService);
            _patchService = new PatchService(_objectService, _writeService);
            _batchService = new BatchService(_kbService, _writeService, _patchService, _objectService);
            _forgeService = new ForgeService(_kbService);
            _testService = new TestService(_kbService, _buildService);
            _wikiService = new WikiService(_objectService, _searchService);
            _historyService = new HistoryService(_objectService, _writeService);
            _linterService = new LinterService(_objectService, _navigationService);
            _patternService = new PatternService(_indexCacheService, _objectService);
            _sdtService = new SDTService(_objectService);
            _structureService = new StructureService(_objectService);
            _propertyService = new PropertyService(_objectService);
            _conversionService = new ConversionService(_objectService);
            _selfTestService = new SelfTestService(_kbService, _searchService, _linterService);

            // Phase 2: Late Linking
            _kbService.SetBuildService(_buildService);
            _buildService.SetKbService(_kbService);
            _indexCacheService.SetBuildService(_buildService);
            _validationService.SetObjectService(_objectService);
            _writeService.SetValidationService(_validationService);
            _objectService.SetDataInsightService(_dataInsightService);
            _objectService.SetUIService(_uiService);
        }

        public static CommandDispatcher Instance
        {
            get { lock (_lock) { return _instance ?? (_instance = new CommandDispatcher()); } }
        }

        public KbService GetKbService() { return _kbService; }

        public bool IsThreadSafe(string line)
        {
            try
            {
                var request = JObject.Parse(line);
                string method = request["method"] != null ? request["method"].ToString() : null;
                if (method == "ping") return true;
                return true;
            }
            catch { return false; }
        }

        public string Dispatch(string line)
        {
            try
            {
                var request = JObject.Parse(line);
                string method = request["method"]?.ToString();
                string action = request["action"]?.ToString();
                string target = request["target"]?.ToString();
                var payload = request["payload"]?.ToString();
                var args = request["params"] as JObject;

                Logger.Info(string.Format("[DISPATCHER] Method: {0}, Action: {1}, Target: {2}", method, action, target));

                // Nirvana v19.4 Fix: Unwrap Gateway execute_command
                if (method == "execute_command")
                {
                    var inner = request["params"] as JObject;
                    if (inner != null)
                    {
                        method = inner["module"]?.ToString() ?? method;
                        action = inner["action"]?.ToString() ?? action;
                        target = inner["target"]?.ToString() ?? target;
                        payload = inner["payload"]?.ToString() ?? inner["content"]?.ToString() ?? payload;
                        args = inner;
                    }
                }

                switch (method?.ToLower())
                {
                    case "ping": return "{\"status\":\"pong\"}";
                    case "kb":
                        if (action == "Open") return _kbService.OpenKB(target);
                        if (action == "BulkIndex") return _kbService.BulkIndex();
                        if (action == "SelfTest") return _selfTestService.RunAllTests();
                        if (action == "GetIndexStatus") return _kbService.GetIndexStatus();
                        break;
                    case "batch":
                        if (action == "BatchEdit") return _batchService.BatchEdit(target, args?["changes"] as JArray);
                        if (action == "MultiEdit") return _batchService.MultiEdit(args?["items"] as JArray);
                        if (action == "Process") return _batchService.ProcessBatch(args?["batchAction"]?.ToString(), target, payload);
                        break;
                    case "search":
                        if (action == "Query") return _searchService.Search(target, null, null, args?["limit"]?.ToObject<int?>() ?? 50);
                        break;
                    case "read":
                        if (action == "ExtractSource") return _objectService.ReadObjectSource(target, args?["part"]?.ToString(), args?["offset"]?.ToObject<int?>(), args?["limit"]?.ToObject<int?>(), "mcp");
                        if (action == "GetVariables") return _analyzeService.GetVariables(target);
                        if (action == "GetAttribute") return _analyzeService.GetAttributeMetadata(target);
                        break;
                    case "object":
                        if (action == "Read") return _objectService.ReadObject(target);
                        if (action == "Create") return _objectService.CreateObject(args?["type"]?.ToString(), target);
                        break;
                    case "write":
                        return _writeService.WriteObject(target, action, payload);
                    case "patch":
                        if (action == "Apply") return _patchService.ApplyPatch(target, args?["part"]?.ToString(), args?["operation"]?.ToString(), payload, args?["context"]?.ToString());
                        break;
                    case "analyze":
                        if (action == "GetNavigation") return _navigationService.GetNavigation(target);
                        if (action == "GetParameters") return _analyzeService.GetSignature(target);
                        if (action == "GetHierarchy") return _analyzeService.GetHierarchy(target);
                        if (action == "GetDataContext") return _dataInsightService.GetDataContext(target);
                        if (action == "GetConversionContext") return _analyzeService.GetConversionContext(target);
                        if (action == "GetPatternMetadata") return _patternAnalysisService.GetWWPStructure(target);
                        if (action == "Summarize") return _summarizeService.Summarize(target);
                        if (action == "InjectContext") 
                        {
                            bool recursive = args?["recursive"]?.ToObject<bool>() ?? false;
                            return _injectionService.InjectContext(target, recursive);
                        }
                        return _analyzeService.Analyze(target);
                    case "linter":
                        return _linterService.Lint(target);
                    case "ui":
                        if (action == "GetUIContext") return _uiService.GetUIContext(target);
                        break;
                    case "build":
                        if (action == "Build") return _buildService.Build(action, target);
                        if (action == "RebuildAll") return _buildService.Build("RebuildAll", null);
                        if (action == "Sync") return _buildService.Build("BuildAll", null);
                        break;
                    case "validation":
                        return _validationService.ValidateCode(target, action, payload);
                    case "test":
                        return _testService.RunTest(target);
                    case "wiki":
                        return _wikiService.Generate(target);
                    case "visualizer":
                        return _visualizerService.GenerateGraph(payload);
                    case "health":
                        return _healthService.GetHealthReport();
                    case "history":
                        int verId = args?["versionId"]?.ToObject<int>() ?? 0;
                        return _historyService.Execute(target, action, verId);
                    case "property":
                        return _propertyService.GetProperties(target, request["control"]?.ToString());
                }

                return "{\"error\":\"Method or Action not found\"}";
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public static string EscapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}
