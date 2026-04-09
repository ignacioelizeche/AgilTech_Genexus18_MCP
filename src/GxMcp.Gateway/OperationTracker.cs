using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    internal sealed class OperationTracker
    {
        private readonly TimeSpan _retention;
        private readonly ConcurrentDictionary<string, OperationRecord> _operations = new ConcurrentDictionary<string, OperationRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _requestToOperation = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ToolMetricState> _toolMetrics = new ConcurrentDictionary<string, ToolMetricState>(StringComparer.OrdinalIgnoreCase);

        public OperationTracker(TimeSpan retention)
        {
            _retention = retention;
        }

        public string StartOperation(string requestId, string toolName, JObject? toolArguments, string correlationId)
        {
            string operationId = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;
            var record = new OperationRecord
            {
                OperationId = operationId,
                RequestId = requestId,
                ToolName = string.IsNullOrWhiteSpace(toolName) ? "unknown" : toolName,
                CorrelationId = correlationId,
                Status = "Running",
                StartedAtUtc = now,
                UpdatedAtUtc = now,
                ToolArguments = toolArguments != null ? (JObject)toolArguments.DeepClone() : null
            };

            _operations[operationId] = record;
            _requestToOperation[requestId] = operationId;
            return operationId;
        }

        public void MarkTimeout(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId)) return;
            if (!_operations.TryGetValue(operationId, out var record)) return;

            lock (record.SyncRoot)
            {
                record.TimeoutCount++;
                record.TimedOut = true;
                record.UpdatedAtUtc = DateTime.UtcNow;
                if (record.Status == "Running")
                {
                    record.LastError = "Gateway timeout waiting for worker response.";
                }
            }

            if (_toolMetrics.TryGetValue(record.ToolName, out var metric))
            {
                metric.RegisterTimeout();
            }
        }

        public void CompleteFromWorker(string requestId, JObject workerPayload)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return;
            if (!_requestToOperation.TryGetValue(requestId, out var operationId)) return;
            if (!_operations.TryGetValue(operationId, out var record)) return;

            DateTime now = DateTime.UtcNow;
            lock (record.SyncRoot)
            {
                bool isErrorEnvelope = workerPayload["error"] != null;
                bool isErrorStatus = string.Equals(workerPayload["result"]?["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase);
                record.Status = (isErrorEnvelope || isErrorStatus) ? "Failed" : "Completed";
                record.CompletedAtUtc = now;
                record.UpdatedAtUtc = now;
                record.WorkerPayload = workerPayload.DeepClone();
                record.LastError = isErrorEnvelope
                    ? workerPayload["error"]?.ToString()
                    : (isErrorStatus ? workerPayload["result"]?["error"]?.ToString() ?? workerPayload["result"]?["details"]?.ToString() : record.LastError);
            }

            var metric = _toolMetrics.GetOrAdd(record.ToolName, _ => new ToolMetricState(record.ToolName));
            long elapsedMs = record.CompletedAtUtc.HasValue
                ? Math.Max(0L, (long)(record.CompletedAtUtc.Value - record.StartedAtUtc).TotalMilliseconds)
                : 0L;
            metric.RegisterCompletion(elapsedMs, string.Equals(record.Status, "Failed", StringComparison.OrdinalIgnoreCase), record.WorkerPayload);
        }

        public void MarkFailedByRequest(string requestId, string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return;
            if (!_requestToOperation.TryGetValue(requestId, out var operationId)) return;
            if (!_operations.TryGetValue(operationId, out var record)) return;

            lock (record.SyncRoot)
            {
                record.Status = "Failed";
                record.CompletedAtUtc = DateTime.UtcNow;
                record.UpdatedAtUtc = record.CompletedAtUtc.Value;
                record.LastError = errorMessage;
            }

            var metric = _toolMetrics.GetOrAdd(record.ToolName, _ => new ToolMetricState(record.ToolName));
            long elapsedMs = record.CompletedAtUtc.HasValue
                ? Math.Max(0L, (long)(record.CompletedAtUtc.Value - record.StartedAtUtc).TotalMilliseconds)
                : 0L;
            metric.RegisterCompletion(elapsedMs, isError: true, workerPayload: null);
        }

        public JObject BuildOperationStatus(string operationId)
        {
            if (!_operations.TryGetValue(operationId, out var record))
            {
                return new JObject
                {
                    ["status"] = "NotFound",
                    ["operationId"] = operationId,
                    ["message"] = "Operation not found or expired."
                };
            }

            lock (record.SyncRoot)
            {
                return new JObject
                {
                    ["status"] = record.Status,
                    ["operationId"] = record.OperationId,
                    ["toolName"] = record.ToolName,
                    ["correlationId"] = record.CorrelationId,
                    ["timedOut"] = record.TimedOut,
                    ["timeoutCount"] = record.TimeoutCount,
                    ["startedAtUtc"] = record.StartedAtUtc,
                    ["updatedAtUtc"] = record.UpdatedAtUtc,
                    ["completedAtUtc"] = record.CompletedAtUtc,
                    ["error"] = record.LastError
                };
            }
        }

        public JObject BuildOperationResult(string operationId)
        {
            if (!_operations.TryGetValue(operationId, out var record))
            {
                return new JObject
                {
                    ["status"] = "NotFound",
                    ["operationId"] = operationId,
                    ["message"] = "Operation not found or expired."
                };
            }

            lock (record.SyncRoot)
            {
                var payload = new JObject
                {
                    ["status"] = record.Status,
                    ["operationId"] = record.OperationId,
                    ["toolName"] = record.ToolName,
                    ["correlationId"] = record.CorrelationId,
                    ["timedOut"] = record.TimedOut,
                    ["timeoutCount"] = record.TimeoutCount,
                    ["startedAtUtc"] = record.StartedAtUtc,
                    ["updatedAtUtc"] = record.UpdatedAtUtc,
                    ["completedAtUtc"] = record.CompletedAtUtc
                };

                if (!string.IsNullOrWhiteSpace(record.LastError))
                {
                    payload["error"] = record.LastError;
                }

                if (record.WorkerPayload != null)
                {
                    payload["workerPayload"] = record.WorkerPayload.DeepClone();
                }
                else if (string.Equals(record.Status, "Running", StringComparison.OrdinalIgnoreCase))
                {
                    payload["message"] = "Operation is still running. Query status again later.";
                }

                return payload;
            }
        }

        public JObject BuildMetricsPayload()
        {
            var items = new JArray(
                _toolMetrics.Values
                    .OrderBy(metric => metric.ToolName, StringComparer.OrdinalIgnoreCase)
                    .Select(metric => metric.ToJObject()));

            return new JObject
            {
                ["status"] = "Success",
                ["generatedAtUtc"] = DateTime.UtcNow,
                ["tools"] = items
            };
        }

        public void CleanupExpired()
        {
            DateTime cutoff = DateTime.UtcNow - _retention;
            foreach (var kvp in _operations)
            {
                var record = kvp.Value;
                bool remove;
                lock (record.SyncRoot)
                {
                    remove = record.UpdatedAtUtc < cutoff;
                }

                if (!remove) continue;

                _operations.TryRemove(kvp.Key, out _);
                _requestToOperation.TryRemove(record.RequestId, out _);
            }
        }

        private sealed class OperationRecord
        {
            public readonly object SyncRoot = new object();
            public string OperationId { get; set; } = string.Empty;
            public string RequestId { get; set; } = string.Empty;
            public string ToolName { get; set; } = string.Empty;
            public string CorrelationId { get; set; } = string.Empty;
            public string Status { get; set; } = "Running";
            public bool TimedOut { get; set; }
            public int TimeoutCount { get; set; }
            public DateTime StartedAtUtc { get; set; }
            public DateTime UpdatedAtUtc { get; set; }
            public DateTime? CompletedAtUtc { get; set; }
            public string? LastError { get; set; }
            public JObject? ToolArguments { get; set; }
            public JToken? WorkerPayload { get; set; }
        }

        private sealed class ToolMetricState
        {
            private readonly object _lock = new object();
            private readonly List<long> _latencies = new List<long>();
            private const int MaxLatencySamples = 256;

            public ToolMetricState(string toolName)
            {
                ToolName = toolName;
            }

            public string ToolName { get; }
            public long Count { get; private set; }
            public long ErrorCount { get; private set; }
            public long TimeoutCount { get; private set; }
            public long NoChangeCount { get; private set; }
            public long PatchFailCount { get; private set; }
            public long FallbackSaveCount { get; private set; }

            public void RegisterTimeout()
            {
                lock (_lock)
                {
                    TimeoutCount++;
                }
            }

            public void RegisterCompletion(long elapsedMs, bool isError, JToken? workerPayload)
            {
                lock (_lock)
                {
                    Count++;
                    if (isError) ErrorCount++;
                    if (elapsedMs > 0)
                    {
                        _latencies.Add(elapsedMs);
                        if (_latencies.Count > MaxLatencySamples)
                        {
                            _latencies.RemoveAt(0);
                        }
                    }

                    ApplySemanticCounters(workerPayload);
                }
            }

            public JObject ToJObject()
            {
                lock (_lock)
                {
                    var ordered = _latencies.OrderBy(v => v).ToArray();
                    long p50 = Percentile(ordered, 0.50);
                    long p95 = Percentile(ordered, 0.95);
                    return new JObject
                    {
                        ["toolName"] = ToolName,
                        ["count"] = Count,
                        ["errors"] = ErrorCount,
                        ["timeouts"] = TimeoutCount,
                        ["noChange"] = NoChangeCount,
                        ["patchFail"] = PatchFailCount,
                        ["fallbackSave"] = FallbackSaveCount,
                        ["p50Ms"] = p50,
                        ["p95Ms"] = p95
                    };
                }
            }

            private void ApplySemanticCounters(JToken? workerPayload)
            {
                if (workerPayload == null) return;

                foreach (var obj in EnumerateObjects(workerPayload))
                {
                    string? status = obj["status"]?.ToString();
                    string? details = obj["details"]?.ToString();
                    string? patchStatus = obj["patchStatus"]?.ToString();
                    string? retryStrategy = obj["retryStrategy"]?.ToString();

                    if (string.Equals(status, "NoChange", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(patchStatus, "NoChange", StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(details) && details.IndexOf("No change", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        NoChangeCount++;
                    }

                    if (string.Equals(patchStatus, "NoMatch", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(patchStatus, "Ambiguous", StringComparison.OrdinalIgnoreCase))
                    {
                        PatchFailCount++;
                    }

                    if (!string.IsNullOrWhiteSpace(retryStrategy) &&
                        retryStrategy.IndexOf("object_save_only", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        FallbackSaveCount++;
                    }
                }
            }

            private static IEnumerable<JObject> EnumerateObjects(JToken token)
            {
                if (token is JObject obj)
                {
                    yield return obj;
                    foreach (var property in obj.Properties())
                    {
                        foreach (var nested in EnumerateObjects(property.Value))
                        {
                            yield return nested;
                        }
                    }
                    yield break;
                }

                if (token is JArray arr)
                {
                    foreach (var item in arr)
                    {
                        foreach (var nested in EnumerateObjects(item))
                        {
                            yield return nested;
                        }
                    }
                }
            }

            private static long Percentile(long[] ordered, double percentile)
            {
                if (ordered.Length == 0) return 0;
                int idx = (int)Math.Ceiling(percentile * ordered.Length) - 1;
                if (idx < 0) idx = 0;
                if (idx >= ordered.Length) idx = ordered.Length - 1;
                return ordered[idx];
            }
        }
    }
}
