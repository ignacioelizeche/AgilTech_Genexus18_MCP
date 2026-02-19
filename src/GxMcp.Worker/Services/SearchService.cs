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
            { "acadêmico", new[] { "acad" } },
            { "academico", new[] { "acad" } },
            { "wrf", new[] { "workflow", "fluxo", "etapa" } },
            { "fluxo", new[] { "wrf" } },
            { "trn", new[] { "transação", "transacao", "transaction" } },
            { "prc", new[] { "procedure", "processo" } },
            { "att", new[] { "atributo", "attribute" } },
            { "tbl", new[] { "tabela", "table" } },
            { "dom", new[] { "domínio", "dominio" } }
        };

        public string Search(string query)
        {
            try
            {
                var index = _indexCacheService.GetIndex();
                if (index == null || index.Objects.Count == 0)
                    return "{\"error\": \"Search index not found or empty. To use discovery tools, you MUST first run 'genexus_bulk_index' to synchronize KB metadata.\"}";

                string[] originalTerms = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var expandedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                
                foreach (var term in originalTerms)
                {
                    expandedTerms.Add(term);
                    if (BusinessSynonyms.TryGetValue(term, out var synonyms))
                    {
                        foreach (var syn in synonyms) expandedTerms.Add(syn);
                    }
                }

                string[] terms = expandedTerms.ToArray();
                var results = new List<RankedResult>();

                foreach (var entry in index.Objects.Values)
                {
                    int score = CalculateScore(entry, terms);
                    if (score > 0)
                    {
                        results.Add(new RankedResult { Entry = entry, Score = score });
                    }
                }

                var topResults = results
                    .OrderByDescending(r => r.Score)
                    .Take(50)
                    .Select(r => {
                        var dict = new Dictionary<string, object>();
                        dict["name"] = r.Entry.Name;
                        dict["type"] = r.Entry.Type;
                        dict["score"] = r.Score;
                        if (r.Entry.BusinessDomain != null && r.Entry.BusinessDomain != "Geral") dict["domain"] = r.Entry.BusinessDomain;
                        if (r.Entry.Rules != null && r.Entry.Rules.Count > 0) dict["rules"] = r.Entry.Rules;
                        if (r.Entry.Tags != null && r.Entry.Tags.Count > 0) dict["tags"] = r.Entry.Tags;
                        if (!string.IsNullOrEmpty(r.Entry.SourceSnippet)) dict["snippet"] = CommandDispatcher.EscapeJsonString(r.Entry.SourceSnippet);
                        dict["connections"] = r.Entry.Calls.Count + r.Entry.CalledBy.Count;
                        return dict;
                    })
                    .ToList();

                return Newtonsoft.Json.JsonConvert.SerializeObject(new { 
                    count = topResults.Count, 
                    results = topResults 
                });
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private int CalculateScore(SearchIndex.IndexEntry entry, string[] terms)
        {
            int score = 0;
            string content = $"{entry.Name} {entry.Description} {entry.BusinessDomain} {string.Join(" ", entry.Tags)} {string.Join(" ", entry.Keywords)} {string.Join(" ", entry.Rules)} {entry.SourceSnippet}";
            
            // Normalize name (remove types if present in comparison)
            string pureName = entry.Name.Contains(":") ? entry.Name.Split(':')[1] : entry.Name;

            foreach (var term in terms)
            {
                // Exact name match (Highest priority)
                if (pureName.Equals(term, StringComparison.OrdinalIgnoreCase)) score += 200;
                
                // Prefix match
                if (pureName.StartsWith(term, StringComparison.OrdinalIgnoreCase)) score += 100;

                // Substring match in name
                if (pureName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) score += 50;

                // Type match boost
                if (IsTypeMatch(entry.Type, term)) score += 150;

                // Domain match
                if (entry.BusinessDomain != null && entry.BusinessDomain.Equals(term, StringComparison.OrdinalIgnoreCase)) score += 50;

                // Tag match
                if (entry.Tags.Any(tag => tag.Equals(term, StringComparison.OrdinalIgnoreCase))) score += 40;

                // Rule match
                if (entry.Rules.Any(r => r.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)) score += 35;

                // Keyword match (from name segments)
                if (entry.Keywords != null && entry.Keywords.Any(k => k.Equals(term, StringComparison.OrdinalIgnoreCase))) score += 30;

                // Source code match
                if (!string.IsNullOrEmpty(entry.SourceSnippet) && entry.SourceSnippet.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) score += 15;

                // Keyword/Description match (content search)
                if (content.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) score += 5;
            }

            // Graph Ranking: Objects with more connections are more relevant
            if (score > 0)
            {
                score += (entry.Calls.Count * 2);      // Hubiness
                score += (entry.CalledBy.Count * 10);  // Authority (weighted more heavily now)
            }

            return score;
        }

        private bool IsTypeMatch(string type, string term)
        {
            string t = type.ToLower();
            string q = term.ToLower();

            if (q == "prc" || q == "proc" || q == "procedure") return t == "procedure" || t == "prc";
            if (q == "trn" || q == "transaction") return t == "transaction" || t == "trn";
            if (q == "wp" || q == "wbp" || q == "webpanel") return t == "webpanel" || t == "wbp";
            if (q == "att" || q == "attribute") return t == "attribute" || t == "att";
            if (q == "tbl" || q == "table") return t == "table" || t == "tbl";
            if (q == "dom" || q == "domain") return t == "domain" || t == "dom";
            if (q == "pnl" || q == "panel") return t == "webpanel" || t == "sdpanel" || t == "wbp" || t == "sdp";

            return false;
        }

        private class RankedResult
        {
            public SearchIndex.IndexEntry Entry { get; set; }
            public int Score { get; set; }
        }
    }
}
