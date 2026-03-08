using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class BatchService
    {
        private readonly KbService _kbService;
        private readonly WriteService _writeService;
        private readonly PatchService _patchService;
        private readonly ObjectService _objectService;
        private readonly List<BatchItem> _buffer = new List<BatchItem>();

        public BatchService(KbService kbService, WriteService writeService, PatchService patchService, ObjectService objectService)
        {
            _kbService = kbService;
            _writeService = writeService;
            _patchService = patchService;
            _objectService = objectService;
        }

        public string BatchEdit(string target, JArray changes)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int count = 0;
                var results = new JArray();

                foreach (var change in changes)
                {
                    string part = change["part"]?.ToString() ?? "Source";
                    string mode = change["mode"]?.ToString() ?? "patch";
                    string content = change["content"]?.ToString();
                    string context = change["context"]?.ToString();
                    string operation = change["operation"]?.ToString() ?? "Replace";

                    string result;
                    if (mode == "patch")
                    {
                        result = _patchService.ApplyPatch(target, part, operation, content, context);
                    }
                    else
                    {
                        result = _writeService.WriteObject(target, part, content);
                    }
                    
                    try {
                        results.Add(JObject.Parse(result));
                    } catch {
                        results.Add(new JObject { ["error"] = result });
                    }
                    count++;
                }

                return new JObject { 
                    ["status"] = "Success", 
                    ["count"] = count, 
                    ["results"] = results,
                    ["duration"] = sw.ElapsedMilliseconds 
                }.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"BatchEdit failed: " + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string ProcessBatch(string action, string name, string code)
        {
            if (action == "Add")
            {
                _buffer.Add(new BatchItem { Name = name, Code = code });
                return "{\"status\":\"Buffered\"}";
            }
            else if (action == "Commit")
            {
                int count = 0;
                foreach (var item in _buffer)
                {
                    _writeService.WriteObject(item.Name, "Source", item.Code);
                    count++;
                }
                _buffer.Clear();
                return "{\"status\":\"Committed\", \"count\":" + count + "}";
            }
            return "{\"error\":\"Unknown action\"}";
        }

        private class BatchItem { public string Name; public string Code; }

        public string MultiEdit(JArray items)
        {
            try
            {
                if (items == null || items.Count == 0)
                    return "{\"error\":\"No items provided\"}";

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var allResults = new JArray();
                int totalChanges = 0;

                foreach (var item in items)
                {
                    string name = item["name"]?.ToString();
                    var changes = item["changes"] as JArray;
                    if (string.IsNullOrEmpty(name) || changes == null) continue;

                    string result = BatchEdit(name, changes);
                    try {
                        var parsed = JObject.Parse(result);
                        parsed["object"] = name;
                        allResults.Add(parsed);
                        totalChanges += parsed["count"]?.ToObject<int>() ?? 0;
                    } catch {
                        allResults.Add(new JObject { ["object"] = name, ["error"] = result });
                    }
                }

                return new JObject {
                    ["status"] = "Success",
                    ["objectCount"] = items.Count,
                    ["totalChanges"] = totalChanges,
                    ["results"] = allResults,
                    ["duration"] = sw.ElapsedMilliseconds
                }.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"MultiEdit failed: " + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }
        public string BatchRead(JArray items)
        {
            try
            {
                if (items == null || items.Count == 0) return "{\"error\":\"No items provided\"}";

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var results = new JArray();

                foreach (var item in items)
                {
                    string name = item["name"]?.ToString();
                    string part = item["part"]?.ToString() ?? "Source";
                    if (string.IsNullOrEmpty(name)) continue;

                    string readResult = _objectService.ReadObjectSource(name, part, null, null, "mcp");
                    try {
                        var parsed = JObject.Parse(readResult);
                        parsed["object"] = name;
                        results.Add(parsed);
                    } catch {
                        results.Add(new JObject { ["object"] = name, ["error"] = readResult });
                    }
                }

                return new JObject {
                    ["status"] = "Success",
                    ["count"] = results.Count,
                    ["results"] = results,
                    ["duration"] = sw.ElapsedMilliseconds
                }.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"BatchRead failed: " + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }
    }
}