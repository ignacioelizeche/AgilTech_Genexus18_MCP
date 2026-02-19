using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class SearchService
    {
        private readonly string _indexPath;

        public SearchService()
        {
            _indexPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "search_index.json");
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
                if (!File.Exists(_indexPath))
                    return "{\"status\": \"No search index found. Run genexus_analyze first to populate index.\"}";

                string json = File.ReadAllText(_indexPath);

                var index = SearchIndex.FromJson(json);
                if (index == null || index.Objects.Count == 0)
                    return "{\"status\": \"Search index is empty.\"}";

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
                    .Take(10)
                    .Select(r => new {
                        name = r.Entry.Name,
                        type = r.Entry.Type,
                        score = r.Score,
                        domain = r.Entry.BusinessDomain ?? "Geral",
                        rules = r.Entry.Rules,
                        tags = r.Entry.Tags,
                        snippet = CommandDispatcher.EscapeJsonString(r.Entry.SourceSnippet ?? ""),
                        connections = r.Entry.Calls.Count + r.Entry.CalledBy.Count
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
            
            foreach (var term in terms)
            {
                // Exact name match (Highest priority)
                if (entry.Name.Equals(term, StringComparison.OrdinalIgnoreCase)) score += 100;
                
                // Name contains term
                if (entry.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) score += 20;

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
                score += (entry.CalledBy.Count * 5);   // Authority (more objects call it)
            }

            return score;
        }

        private class RankedResult
        {
            public SearchIndex.IndexEntry Entry { get; set; }
            public int Score { get; set; }
        }
    }
}
