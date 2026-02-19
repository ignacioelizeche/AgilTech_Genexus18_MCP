using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace GxMcp.Worker.Models
{
    public class SearchIndex
    {
        public Dictionary<string, IndexEntry> Objects { get; set; } = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
        public DateTime LastUpdated { get; set; }

        public class IndexEntry
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public string Description { get; set; }
            public List<string> Tags { get; set; } = new List<string>();
            public List<string> Keywords { get; set; } = new List<string>();
            
            // Graph Relationships
            public List<string> Calls { get; set; } = new List<string>();
            public List<string> CalledBy { get; set; } = new List<string>();
            public List<string> Tables { get; set; } = new List<string>();
            public List<string> Rules { get; set; } = new List<string>();
            
            // Business Intelligence fields
            public string BusinessDomain { get; set; }
            public string ConceptualSummary { get; set; }
            
            public string SourceSnippet { get; set; }
            public int Complexity { get; set; }
        }

        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);
        public static SearchIndex FromJson(string json) => JsonConvert.DeserializeObject<SearchIndex>(json);
    }
}
