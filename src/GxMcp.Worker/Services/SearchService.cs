using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class SearchService
    {
        private readonly IndexCacheService _indexCacheService;

        public SearchService(IndexCacheService indexCacheService)
        {
            _indexCacheService = indexCacheService;
        }

        private static readonly Dictionary<string, string[]> BusinessSynonyms = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "ptc", new[] { "protocolo" } },
            { "protocolo", new[] { "ptc" } },
            { "fin", new[] { "financeiro" } },
            { "financeiro", new[] { "fin" } },
            { "acad", new[] { "acadêmico", "academico", "estudante", "aluno" } },
            { "wrf", new[] { "workflow", "fluxo", "etapa" } },
            { "trn", new[] { "transação", "transacao", "transaction" } },
            { "prc", new[] { "procedure", "processo" } },
            { "att", new[] { "atributo", "attribute" } },
            { "tbl", new[] { "tabela", "table" } },
            { "dom", new[] { "domínio", "dominio" } }
        };

        public string Search(string query, string typeFilter = null, string domainFilter = null, int limit = 50)
        {
            try
            {
                var index = _indexCacheService.GetIndex();
                if (index == null || index.Objects.Count == 0)
                    return "{\"error\": \"Search index not found or empty. Please run 'genexus_bulk_index' first.\"}";

                // Parse structured query: type:X calls:Y uses:Z
                var criteria = ParseQuery(query);
                if (!string.IsNullOrEmpty(typeFilter)) criteria.TypeFilter = typeFilter;
                if (!string.IsNullOrEmpty(domainFilter)) criteria.DomainFilter = domainFilter;

                var results = new List<RankedResult>();

                foreach (var entry in index.Objects.Values)
                {
                    // 1. Hard Filters (Must match)
                    if (!string.IsNullOrEmpty(criteria.TypeFilter) && !IsTypeMatch(entry.Type, criteria.TypeFilter)) continue;
                    if (!string.IsNullOrEmpty(criteria.DomainFilter) && !string.Equals(entry.BusinessDomain, criteria.DomainFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(criteria.CallsFilter) && !CheckCalls(entry, criteria.CallsFilter)) continue;
                    if (!string.IsNullOrEmpty(criteria.CalledByFilter) && !CheckCalledBy(entry, criteria.CalledByFilter)) continue;
                    if (!string.IsNullOrEmpty(criteria.UsesFilter) && !CheckUses(entry, criteria.UsesFilter)) continue; // Requires 'Uses' in index (TBD)

                    // 2. Text Scoring (Soft match)
                    int score = 0;
                    if (criteria.Terms.Count > 0)
                    {
                        score = CalculateScore(entry, criteria.Terms.ToArray());
                        if (score <= 0) continue;
                    }
                    else
                    {
                        score = 1; // Pure filtering mode
                    }

                    results.Add(new RankedResult { Entry = entry, Score = score });
                }

                var finalResults = results
                    .OrderByDescending(r => r.Score)
                    .ThenBy(r => r.Entry.Name)
                    .Take(limit)
                    .Select(r => {
                        var dict = new Dictionary<string, object>();
                        dict["name"] = r.Entry.Name;
                        dict["type"] = r.Entry.Type;
                        if (r.Score > 1) dict["score"] = r.Score;
                        if (!string.IsNullOrEmpty(r.Entry.Description)) dict["description"] = r.Entry.Description;
                        if (r.Entry.BusinessDomain != null && r.Entry.BusinessDomain != "Geral") dict["domain"] = r.Entry.BusinessDomain;
                        dict["connections"] = (r.Entry.Calls?.Count ?? 0) + (r.Entry.CalledBy?.Count ?? 0);
                        return dict;
                    })
                    .ToList();

                return Newtonsoft.Json.JsonConvert.SerializeObject(new { 
                    count = finalResults.Count, 
                    results = finalResults 
                });
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private SearchCriteria ParseQuery(string query)
        {
            var criteria = new SearchCriteria();
            if (string.IsNullOrEmpty(query)) return criteria;

            var parts = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (part.Contains(":"))
                {
                    var kv = part.Split(new[] { ':' }, 2);
                    string key = kv[0].ToLower();
                    string val = kv[1];

                    switch(key) {
                        case "type": criteria.TypeFilter = val; break;
                        case "domain": criteria.DomainFilter = val; break;
                        case "calls": criteria.CallsFilter = val; break;
                        case "calledby": criteria.CalledByFilter = val; break;
                        case "uses": criteria.UsesFilter = val; break;
                        default: criteria.Terms.Add(part); break; // Treat unknown prefix as literal text
                    }
                }
                else
                {
                    criteria.Terms.Add(part);
                    // Add synonyms for main terms
                    if (BusinessSynonyms.TryGetValue(part, out var synonyms))
                        foreach (var syn in synonyms) criteria.Terms.Add(syn);
                }
            }
            return criteria;
        }

        private bool CheckCalls(SearchIndex.IndexEntry entry, string target)
        {
            if (entry.Calls == null) return false;
            return entry.Calls.Any(c => c.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private bool CheckCalledBy(SearchIndex.IndexEntry entry, string caller)
        {
            if (entry.CalledBy == null) return false;
            return entry.CalledBy.Any(c => c.IndexOf(caller, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private bool CheckUses(SearchIndex.IndexEntry entry, string usage)
        {
            // Placeholder: Our Index currently doesn't store Attribute/Table usage separately from 'Calls'
            // We can check Calls for now as a fallback
            return CheckCalls(entry, usage);
        }

        private int CalculateScore(SearchIndex.IndexEntry entry, string[] terms)
        {
            int score = 0;
            string content = $"{entry.Name} {entry.Description} {entry.BusinessDomain} {string.Join(" ", entry.Tags ?? new List<string>())}";
            
            foreach (var term in terms)
            {
                if (entry.Name.Equals(term, StringComparison.OrdinalIgnoreCase)) score += 500;
                if (entry.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase)) score += 200;
                if (entry.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) score += 100;
                if (IsTypeMatch(entry.Type, term)) score += 150;
                if (content.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) score += 10;
            }
            score += (entry.CalledBy?.Count ?? 0) * 5;
            return score;
        }

        private bool IsTypeMatch(string type, string query)
        {
            string t = type.ToLower();
            string q = query.ToLower();
            if (q == "prc" || q == "proc" || q == "procedure") return t.Contains("procedure") || t == "prc";
            if (q == "trn" || q == "transaction") return t.Contains("transaction") || t == "trn";
            if (q == "wp" || q == "wbp" || q == "webpanel") return t.Contains("webpanel") || t == "wbp";
            if (q == "att" || q == "attribute") return t.Contains("attribute") || t == "att";
            if (q == "tbl" || q == "table") return t.Contains("table") || t == "tbl";
            return t.Contains(q) || q.Contains(t);
        }

        private class RankedResult
        {
            public SearchIndex.IndexEntry Entry { get; set; }
            public int Score { get; set; }
        }

        private class SearchCriteria
        {
            public string TypeFilter { get; set; }
            public string DomainFilter { get; set; }
            public string CallsFilter { get; set; }
            public string CalledByFilter { get; set; }
            public string UsesFilter { get; set; }
            public HashSet<string> Terms { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
