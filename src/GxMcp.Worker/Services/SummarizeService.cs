using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class SummarizeService
    {
        private readonly KbService _kbService;
        private readonly ObjectService _objectService;

        public SummarizeService(KbService kbService, ObjectService objectService)
        {
            _kbService = kbService;
            _objectService = objectService;
        }

        public string Summarize(string target, string typeFilter = null)
        {
            try
            {
                var obj = _objectService.FindObject(target, typeFilter);
                if (obj == null) return Models.McpResponse.Error("Object not found", target, null, "The requested object is not available in the active Knowledge Base.");

                var result = new JObject();
                result["name"] = obj.Name;
                result["type"] = obj.TypeDescriptor.Name;
                result["description"] = obj.Description;

                // 1. Signature & Parms
                var (parmRule, parms) = _objectService.GetParametersInternal(obj);
                result["parmRule"] = parmRule;
                var parmList = new JArray();
                foreach (var p in parms) parmList.Add(new JObject { ["name"] = p.Name, ["accessor"] = p.Accessor, ["type"] = p.Type });
                result["parameters"] = parmList;

                // 2. Extract Logic Intents
                result["intents"] = ExtractIntents(obj);

                // 3. Key Dependencies (Semantic)
                result["criticalDependencies"] = ExtractCriticalDependencies(obj);

                // 4. Complexity & Risk
                result["metrics"] = CalculateMetrics(obj);

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private JArray ExtractIntents(KBObject obj)
        {
            var intents = new JArray();
            string source = "";
            
            if (obj is Procedure proc) source = proc.ProcedurePart.Source;
            else if (obj is WebPanel wbp) source = wbp.Parts.Get<EventsPart>()?.Source ?? "";
            else if (obj is Transaction trn) source = trn.Parts.Get<EventsPart>()?.Source ?? "";

            if (string.IsNullOrEmpty(source)) return intents;

            // Simple Pattern Matching for common GeneXus logic
            if (source.IndexOf("for each", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var match = Regex.Match(source, @"for each\s+([^\n\r]*)", RegexOptions.IgnoreCase);
                intents.Add($"Iterates over data: {match.Groups[1].Value.Trim()}");
            }

            if (source.IndexOf("new", StringComparison.OrdinalIgnoreCase) >= 0 && source.IndexOf("endnew", StringComparison.OrdinalIgnoreCase) >= 0)
                intents.Add("Inserts new records");

            if (source.IndexOf("delete", StringComparison.OrdinalIgnoreCase) >= 0)
                intents.Add("Deletes records");

            if (source.IndexOf("call(", StringComparison.OrdinalIgnoreCase) >= 0 || source.IndexOf(".call(", StringComparison.OrdinalIgnoreCase) >= 0)
                intents.Add("Orchestrates other processes");

            if (source.IndexOf("msg(", StringComparison.OrdinalIgnoreCase) >= 0)
                intents.Add("Communicates with user/logs");

            if (source.IndexOf("error(", StringComparison.OrdinalIgnoreCase) >= 0)
                intents.Add("Includes validation logic");

            return intents;
        }

        private JArray ExtractCriticalDependencies(KBObject obj)
        {
            var deps = new JArray();
            var kb = _kbService.GetKB();
            if (kb == null) return deps;

            // Get all references and take distinctive target names
            var references = obj.GetReferences()
                .Select(r => kb.DesignModel.Objects.Get(r.To)?.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .Take(10);

            foreach (var name in references) deps.Add(name);
            return deps;
        }

        private JObject CalculateMetrics(KBObject obj)
        {
            var metrics = new JObject();
            string source = "";
            if (obj is Procedure p) source = p.ProcedurePart.Source;
            else if (obj is WebPanel w) source = w.Parts.Get<EventsPart>()?.Source ?? "";
            else if (obj is Transaction t) source = t.Parts.Get<EventsPart>()?.Source ?? "";

            int lines = string.IsNullOrEmpty(source) ? 0 : source.Split('\n').Length;
            metrics["linesOfCode"] = lines;
            metrics["complexity"] = lines > 500 ? "High" : (lines > 100 ? "Medium" : "Low");
            
            return metrics;
        }
    }
}
