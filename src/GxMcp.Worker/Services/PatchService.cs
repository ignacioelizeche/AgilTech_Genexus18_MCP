using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Diagnostics;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Structure;

namespace GxMcp.Worker.Services
{
    public class PatchService
    {
        private sealed class SourceCacheEntry
        {
            public string Source { get; set; }
            public DateTime UpdatedUtc { get; set; }
        }

        private static readonly ConcurrentDictionary<string, SourceCacheEntry> _sourceCache =
            new ConcurrentDictionary<string, SourceCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan SourceCacheTtl = TimeSpan.FromSeconds(20);

        private readonly ObjectService _objectService;
        private readonly WriteService _writeService;

        public PatchService(ObjectService objectService, WriteService writeService)
        {
            _objectService = objectService;
            _writeService = writeService;
        }

        public string ApplyPatch(string target, string partName, string operation, string content, string context = null, int expectedCount = 1, string typeFilter = null, bool dryRun = false, bool verifyRollback = false)
        {
            try
            {
                string cacheKey = BuildCacheKey(target, partName, typeFilter);
                bool sourceFromCache = false;
                long readMs = 0;
                string originalSource = TryGetCachedSource(cacheKey);
                if (originalSource == null)
                {
                    var readStopwatch = Stopwatch.StartNew();
                    string currentResponse = ReadSourceFast(target, partName, typeFilter);
                    readStopwatch.Stop();
                    readMs = readStopwatch.ElapsedMilliseconds;
                    string readError = TryExtractError(currentResponse);
                    if (!string.IsNullOrWhiteSpace(readError))
                    {
                        return Models.McpResponse.Error("Patch read failed", target, partName, readError);
                    }

                    var json = JObject.Parse(currentResponse);
                    originalSource = json["source"]?.ToString();
                    if (originalSource == null)
                    {
                        return Models.McpResponse.Error("Patch read failed", target, partName, "Could not retrieve source for requested part.");
                    }
                    UpdateCachedSource(cacheKey, originalSource);
                }
                else
                {
                    sourceFromCache = true;
                }

                // Normalize line endings for internal processing
                string workSource = originalSource.Replace("\r\n", "\n").Replace("\r", "\n");
                string workContext = context?.Replace("\r\n", "\n").Replace("\r", "\n");
                string workContent = (content ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");

                var sourceLines = workSource.Split('\n');
                var contextLines = workContext?.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                string normalizedOperation = NormalizeOperation(operation);

                // 2. Matching Logic
                string updatedSource = null;
                int matchCount = 0;
                string status = null;
                string details = null;

                if (expectedCount <= 0)
                {
                    return BuildPatchResult("Error", partName, normalizedOperation, expectedCount, 0, "expectedCount must be >= 1.");
                }

                var patchStopwatch = Stopwatch.StartNew();
                switch (normalizedOperation)
                {
                    case "replace":
                        if (string.IsNullOrEmpty(workContext))
                            return BuildPatchResult("Error", partName, normalizedOperation, expectedCount, 0, "'context' (old_string) is required for Replace.");

                        if (NormalizeSourceForComparison(workContext) == NormalizeSourceForComparison(workContent))
                        {
                            return BuildPatchResult("NoChange", partName, normalizedOperation, expectedCount, 1, "Patch content is identical to context. Write skipped.");
                        }

                        updatedSource = TryReplace(sourceLines, contextLines ?? new string[0], workContent, expectedCount, out status, out details, out matchCount);
                        break;

                    case "insert_after":
                        if (string.IsNullOrEmpty(workContext))
                            return BuildPatchResult("Error", partName, normalizedOperation, expectedCount, 0, "'context' (anchor) is required for Insert_After.");
                        updatedSource = TryInsertAfter(sourceLines, contextLines ?? new string[0], workContent, expectedCount, out status, out details, out matchCount);
                        break;

                    case "append":
                        matchCount = 1;
                        if (string.IsNullOrWhiteSpace(workContent))
                        {
                            status = "NoChange";
                            details = "Append payload is empty. Write skipped.";
                            updatedSource = workSource;
                            break;
                        }
                        updatedSource = workSource.TrimEnd() + "\n" + workContent;
                        status = "Applied";
                        break;

                    default:
                        return BuildPatchResult("Error", partName, normalizedOperation, expectedCount, 0, "Unknown operation: " + operation);
                }
                patchStopwatch.Stop();
                long patchMs = patchStopwatch.ElapsedMilliseconds;

                // One guarded retry against stale cache: refresh source once and recompute.
                if (sourceFromCache &&
                    string.IsNullOrEmpty(updatedSource) &&
                    (string.Equals(status, "NoMatch", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(status, "Ambiguous", StringComparison.OrdinalIgnoreCase)))
                {
                    var refreshReadSw = Stopwatch.StartNew();
                    string refreshedResponse = ReadSourceFast(target, partName, typeFilter);
                    refreshReadSw.Stop();
                    readMs += refreshReadSw.ElapsedMilliseconds;
                    string refreshedError = TryExtractError(refreshedResponse);
                    if (string.IsNullOrWhiteSpace(refreshedError))
                    {
                        var refreshedJson = JObject.Parse(refreshedResponse);
                        string refreshedSource = refreshedJson["source"]?.ToString();
                        if (refreshedSource != null)
                        {
                            UpdateCachedSource(cacheKey, refreshedSource);
                            originalSource = refreshedSource;
                            workSource = originalSource.Replace("\r\n", "\n").Replace("\r", "\n");
                            sourceLines = workSource.Split('\n');
                            patchStopwatch.Restart();
                            if (normalizedOperation == "replace")
                            {
                                updatedSource = TryReplace(sourceLines, contextLines ?? new string[0], workContent, expectedCount, out status, out details, out matchCount);
                            }
                            else if (normalizedOperation == "insert_after")
                            {
                                updatedSource = TryInsertAfter(sourceLines, contextLines ?? new string[0], workContent, expectedCount, out status, out details, out matchCount);
                            }
                            patchStopwatch.Stop();
                            patchMs += patchStopwatch.ElapsedMilliseconds;
                        }
                    }
                }

                if (string.IsNullOrEmpty(updatedSource))
                {
                    string dbg = string.Empty;
                    if (sourceLines.Length > 0 && contextLines?.Length > 0)
                    {
                        dbg = $" | Example: Source='{sourceLines[0].Trim()}' vs Context='{contextLines[0].Trim()}'";
                    }
                    Logger.Warn($"[PATCH] Match failed for {target}. Context: '{context}'{dbg}");

                    string failedStatus = string.IsNullOrWhiteSpace(status) ? "NoMatch" : status;
                    string failedDetails = string.IsNullOrWhiteSpace(details)
                        ? $"Context not found. Ensure the context matches a unique block in the source code.{dbg}"
                        : details;
                    string failure = BuildPatchResult(failedStatus, partName, normalizedOperation, expectedCount, matchCount, failedDetails);
                    return AttachTimings(failure, readMs, patchMs, 0, sourceFromCache);
                }

                if (NormalizeSourceForComparison(workSource) == NormalizeSourceForComparison(updatedSource))
                {
                    string noChange = BuildPatchResult("NoChange", partName, normalizedOperation, expectedCount, matchCount, "Patch produced no effective changes. Write skipped.");
                    return AttachTimings(noChange, readMs, patchMs, 0, sourceFromCache);
                }

                if (dryRun)
                {
                    string dryRunResult = BuildPatchResult("Applied", partName, normalizedOperation, expectedCount, matchCount, "Dry-run succeeded. Write skipped.");
                    return AttachTimings(dryRunResult, readMs, patchMs, 0, sourceFromCache);
                }

                // 3. Write Back (re-normalize to CRLF for GeneXus)
                string finalCode = updatedSource.Replace("\n", Environment.NewLine);
                var writeStopwatch = Stopwatch.StartNew();
                string writeResult = _writeService.WriteObject(target, partName, finalCode, typeFilter, autoValidate: false, preferFastSourceSave: true, autoInjectVariables: false);
                writeStopwatch.Stop();
                long writeMs = writeStopwatch.ElapsedMilliseconds;
                JObject writePayload;
                try
                {
                    writePayload = JObject.Parse(writeResult);
                }
                catch
                {
                    writePayload = new JObject { ["status"] = "Error", ["error"] = writeResult };
                }

                if (string.Equals(writePayload["status"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateCachedSource(cacheKey, finalCode);
                }

                if (verifyRollback && string.Equals(writePayload["status"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase))
                {
                    string verifyReadResponse = ReadSourceFast(target, partName, typeFilter);
                    string verifyReadError = TryExtractError(verifyReadResponse);
                    if (!string.IsNullOrWhiteSpace(verifyReadError))
                    {
                        writePayload["status"] = "Error";
                        writePayload["error"] = "Apply verification read failed: " + verifyReadError;
                        writePayload["verifyRollback"] = true;
                    }
                    else
                    {
                        var verifyJson = JObject.Parse(verifyReadResponse);
                        string persistedSource = verifyJson["source"]?.ToString() ?? string.Empty;
                        bool applyVerified = NormalizeSourceForComparison(persistedSource) == NormalizeSourceForComparison(finalCode);
                        writePayload["applyVerified"] = applyVerified;
                        writePayload["verifyRollback"] = true;
                        if (!applyVerified)
                        {
                            writePayload["status"] = "Error";
                            writePayload["error"] = "Apply verification mismatch: persisted content differs from patched content.";
                        }
                    }

                    string rollbackWrite = _writeService.WriteObject(target, partName, originalSource, typeFilter, autoValidate: false, preferFastSourceSave: true, autoInjectVariables: false);
                    JObject rollbackPayload;
                    try
                    {
                        rollbackPayload = JObject.Parse(rollbackWrite);
                    }
                    catch
                    {
                        rollbackPayload = new JObject { ["status"] = "Error", ["error"] = rollbackWrite };
                    }

                    bool rollbackSuccess = string.Equals(rollbackPayload["status"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase);
                    writePayload["rollbackStatus"] = rollbackPayload["status"]?.ToString() ?? "Error";
                    if (!rollbackSuccess)
                    {
                        writePayload["status"] = "Error";
                        writePayload["rollbackError"] = rollbackPayload["error"]?.ToString() ?? "Rollback failed.";
                    }
                    else
                    {
                        string rollbackReadResponse = ReadSourceFast(target, partName, typeFilter);
                        string rollbackReadError = TryExtractError(rollbackReadResponse);
                        if (!string.IsNullOrWhiteSpace(rollbackReadError))
                        {
                            writePayload["status"] = "Error";
                            writePayload["rollbackError"] = "Rollback verification read failed: " + rollbackReadError;
                        }
                        else
                        {
                            var rollbackReadJson = JObject.Parse(rollbackReadResponse);
                            string rollbackSource = rollbackReadJson["source"]?.ToString() ?? string.Empty;
                            bool rollbackVerified = NormalizeSourceForComparison(rollbackSource) == NormalizeSourceForComparison(originalSource);
                            writePayload["rollbackVerified"] = rollbackVerified;
                            if (!rollbackVerified)
                            {
                                writePayload["status"] = "Error";
                                writePayload["rollbackError"] = "Rollback verification mismatch: current content differs from original content.";
                            }
                        }
                    }
                }

                writePayload["patchStatus"] = "Applied";
                writePayload["operation"] = normalizedOperation;
                writePayload["expectedCount"] = expectedCount;
                writePayload["matchCount"] = matchCount;
                writePayload["timings"] = new JObject
                {
                    ["readMs"] = readMs,
                    ["patchMs"] = patchMs,
                    ["writeMs"] = writeMs,
                    ["usedSourceCache"] = sourceFromCache
                };
                return writePayload.ToString();
            }
            catch (Exception ex)
            {
                Logger.Error($"[PATCH] Error applying patch: {ex.Message}");
                return BuildPatchResult("Error", partName, NormalizeOperation(operation), expectedCount, 0, ex.Message);
            }
        }

        private string TryReplace(string[] sourceLines, string[] contextLines, string newContent, int expectedCount, out string status, out string details, out int matchCount)
        {
            status = "Applied";
            details = string.Empty;
            matchCount = 0;

            string source = string.Join("\n", sourceLines);
            string context = string.Join("\n", contextLines);
            
            // 1. Exact match attempt
            int exactCount = CountOccurrences(source, context);
            matchCount = exactCount;
            if (exactCount == expectedCount)
            {
                Logger.Info("[PATCH] Exact match found.");
                return source.Replace(context, newContent);
            }
            if (exactCount > 0)
            {
                status = "Ambiguous";
                details = $"Ambiguous patch: Found {exactCount} exact matches, but expected {expectedCount}. Please provide more context to uniquely identify the block.";
                return string.Empty;
            }

            // 2. Fuzzy match attempt
            Logger.Info("[PATCH] Exact match failed or count mismatch (" + exactCount + " vs " + expectedCount + "). Attempting fuzzy match.");
            var indices = FindFuzzyMatches(sourceLines, contextLines);
            matchCount = indices.Count;
            
            if (indices.Count == expectedCount && indices.Count > 0)
            {
                var resultLines = new List<string>(sourceLines);
                var replacementLines = newContent.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                indices.Sort();
                indices.Reverse();
                foreach (int idx in indices)
                {
                    Logger.Info($"[PATCH] Fuzzy match found at line {idx}.");
                    string indentation = GetIndentation(sourceLines[idx]);
                    var indentedReplacement = ApplyIndentation(replacementLines, indentation);
                    resultLines.RemoveRange(idx, contextLines.Length);
                    resultLines.InsertRange(idx, indentedReplacement);
                }
                return string.Join("\n", resultLines);
            }

            if (indices.Count > 0)
            {
                status = "Ambiguous";
                details = $"Ambiguous patch: Found {indices.Count} fuzzy matches, but expected {expectedCount}. Please provide more context to uniquely identify the block.";
                return string.Empty;
            }

            status = "NoMatch";
            details = "Context block not found.";
            return string.Empty;
        }

        private string TryInsertAfter(string[] sourceLines, string[] contextLines, string newContent, int expectedCount, out string status, out string details, out int matchCount)
        {
            status = "Applied";
            details = string.Empty;
            matchCount = 0;

            var exactIndices = FindExactMatches(sourceLines, contextLines);
            matchCount = exactIndices.Count;
            if (exactIndices.Count == expectedCount && exactIndices.Count > 0)
            {
                return InsertAfterIndices(sourceLines, contextLines, newContent, exactIndices);
            }

            if (exactIndices.Count > 0)
            {
                status = "Ambiguous";
                details = $"Ambiguous anchor: Found {exactIndices.Count} exact matches for the anchor, expected {expectedCount}.";
                return string.Empty;
            }

            var fuzzyIndices = FindFuzzyMatches(sourceLines, contextLines);
            matchCount = fuzzyIndices.Count;
            if (fuzzyIndices.Count == expectedCount && fuzzyIndices.Count > 0)
            {
                return InsertAfterIndices(sourceLines, contextLines, newContent, fuzzyIndices);
            }

            if (fuzzyIndices.Count > 0)
            {
                status = "Ambiguous";
                details = $"Ambiguous anchor: Found {fuzzyIndices.Count} fuzzy matches for the anchor, expected {expectedCount}.";
                return string.Empty;
            }

            status = "NoMatch";
            details = "Anchor block not found.";
            return string.Empty;
        }

        private List<int> FindFuzzyMatches(string[] sourceLines, string[] targetLines)
        {
            var matches = new List<int>();
            if (targetLines.Length == 0 || sourceLines.Length < targetLines.Length) return matches;

            string normalizedFirst = NormalizeWhitespace(targetLines[0]);
            string normalizedLast = NormalizeWhitespace(targetLines[targetLines.Length - 1]);

            for (int i = 0; i <= sourceLines.Length - targetLines.Length; i++)
            {
                if (!string.Equals(NormalizeWhitespace(sourceLines[i]), normalizedFirst, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int tailIndex = i + targetLines.Length - 1;
                if (!string.Equals(NormalizeWhitespace(sourceLines[tailIndex]), normalizedLast, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool match = true;
                for (int j = 0; j < targetLines.Length; j++)
                {
                    if (!LinesMatchFuzzy(sourceLines[i + j], targetLines[j]))
                    {
                        match = false;
                        break;
                    }
                }
                if (match) matches.Add(i);
            }
            return matches;
        }

        private List<int> FindExactMatches(string[] sourceLines, string[] targetLines)
        {
            var matches = new List<int>();
            if (targetLines.Length == 0 || sourceLines.Length < targetLines.Length) return matches;

            for (int i = 0; i <= sourceLines.Length - targetLines.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < targetLines.Length; j++)
                {
                    if (!string.Equals(sourceLines[i + j], targetLines[j], StringComparison.Ordinal))
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    matches.Add(i);
                }
            }

            return matches;
        }

        private string InsertAfterIndices(string[] sourceLines, string[] contextLines, string newContent, List<int> indices)
        {
            var resultLines = new List<string>(sourceLines);
            var insertLinesRaw = newContent.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            indices.Sort();
            indices.Reverse();
            foreach (int idx in indices)
            {
                string indentation = GetIndentation(sourceLines[idx]);
                var indentedInsert = ApplyIndentation(insertLinesRaw, indentation);
                resultLines.InsertRange(idx + contextLines.Length, indentedInsert);
            }

            return string.Join("\n", resultLines);
        }

        private bool LinesMatchFuzzy(string s1, string s2)
        {
            // Normalize: trim and collapse multiples spaces
            string n1 = NormalizeWhitespace(s1);
            string n2 = NormalizeWhitespace(s2);
            return string.Equals(n1, n2, StringComparison.OrdinalIgnoreCase);
        }

        private string NormalizeWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            // Collapse all whitespace (including tabs and multiple spaces) into a single space
            return Regex.Replace(s.Trim(), @"\s+", " ");
        }

        private string GetIndentation(string line)
        {
            var match = Regex.Match(line, @"^(\s*)");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private List<string> ApplyIndentation(IEnumerable<string> contentLines, string indentation)
        {
            var lines = contentLines.ToList();
            if (string.IsNullOrEmpty(indentation)) return lines;
            return lines.Select(line => indentation + line).ToList();
        }

        private int CountOccurrences(string text, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return 0;
            int count = 0, i = 0;
            while ((i = text.IndexOf(pattern, i)) != -1) { i += pattern.Length; count++; }
            return count;
        }

        private static string NormalizeOperation(string operation)
        {
            string value = operation?.Trim().Replace("-", "_").ToLowerInvariant() ?? "replace";
            switch (value)
            {
                case "insertafter":
                case "insert_after":
                    return "insert_after";
                default:
                    return value;
            }
        }

        private static string NormalizeSourceForComparison(string text)
        {
            if (text == null) return string.Empty;
            return text.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
        }

        private static string TryExtractError(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return "Empty response.";

            try
            {
                var json = JObject.Parse(response);
                return json["error"]?.ToString();
            }
            catch
            {
                return "Non-JSON response.";
            }
        }

        private string ReadSourceFast(string target, string partName, string typeFilter)
        {
            try
            {
                var obj = _objectService.FindObject(target, typeFilter);
                if (obj == null)
                {
                    return Models.McpResponse.Error("Object not found", target, partName, "The requested object is not available in the active Knowledge Base.");
                }

                string resolvedPart = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;
                KBObjectPart part = PartAccessor.GetPart(obj, resolvedPart);
                if (part == null)
                {
                    return Models.McpResponse.Error("Part not found", target, resolvedPart, "The object does not expose the requested part.");
                }

                string source = null;
                if (part is ISource sourcePart)
                {
                    source = sourcePart.Source;
                }
                else
                {
                    var contentProp = part.GetType().GetProperty("Source") ?? part.GetType().GetProperty("Content");
                    if (contentProp != null && contentProp.CanRead && contentProp.PropertyType == typeof(string))
                    {
                        source = contentProp.GetValue(part)?.ToString();
                    }
                }

                if (source == null)
                {
                    return Models.McpResponse.Error("Part does not expose text source", target, resolvedPart, "Patch operations require a textual part.");
                }

                return new JObject
                {
                    ["status"] = "Success",
                    ["part"] = resolvedPart,
                    ["source"] = source
                }.ToString();
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Error("Patch read failed", target, partName, ex.Message);
            }
        }

        private static string BuildCacheKey(string target, string partName, string typeFilter)
        {
            string normalizedTarget = target?.Trim() ?? string.Empty;
            string normalizedPart = string.IsNullOrWhiteSpace(partName) ? "Source" : partName.Trim();
            string normalizedType = typeFilter?.Trim() ?? string.Empty;
            return normalizedType + "|" + normalizedTarget + "|" + normalizedPart;
        }

        private static string TryGetCachedSource(string cacheKey)
        {
            if (string.IsNullOrWhiteSpace(cacheKey)) return null;
            if (!_sourceCache.TryGetValue(cacheKey, out var entry) || entry == null) return null;
            if (DateTime.UtcNow - entry.UpdatedUtc > SourceCacheTtl)
            {
                _sourceCache.TryRemove(cacheKey, out _);
                return null;
            }

            return entry.Source;
        }

        private static void UpdateCachedSource(string cacheKey, string source)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || source == null) return;
            _sourceCache[cacheKey] = new SourceCacheEntry
            {
                Source = source,
                UpdatedUtc = DateTime.UtcNow
            };
        }

        private static string AttachTimings(string patchResultJson, long readMs, long patchMs, long writeMs, bool usedSourceCache)
        {
            try
            {
                var json = JObject.Parse(patchResultJson);
                json["timings"] = new JObject
                {
                    ["readMs"] = readMs,
                    ["patchMs"] = patchMs,
                    ["writeMs"] = writeMs,
                    ["usedSourceCache"] = usedSourceCache
                };
                return json.ToString();
            }
            catch
            {
                return patchResultJson;
            }
        }

        private static string BuildPatchResult(string patchStatus, string partName, string operation, int expectedCount, int matchCount, string details)
        {
            bool isError = string.Equals(patchStatus, "NoMatch", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(patchStatus, "Ambiguous", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(patchStatus, "Error", StringComparison.OrdinalIgnoreCase);
            var payload = new JObject
            {
                ["status"] = isError ? "Error" : "Success",
                ["patchStatus"] = patchStatus,
                ["part"] = string.IsNullOrWhiteSpace(partName) ? "Source" : partName,
                ["operation"] = operation,
                ["expectedCount"] = expectedCount,
                ["matchCount"] = matchCount
            };

            if (!string.IsNullOrWhiteSpace(details))
            {
                payload["details"] = details;
            }

            if (isError)
            {
                payload["error"] = details;
            }

            return payload.ToString();
        }
    }
}
