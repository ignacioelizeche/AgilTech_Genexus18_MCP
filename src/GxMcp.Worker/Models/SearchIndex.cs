using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Newtonsoft.Json;

namespace GxMcp.Worker.Models
{
    public class SearchIndex
    {
        public ConcurrentDictionary<string, IndexEntry> Objects { get; set; } = new ConcurrentDictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
        public DateTime LastUpdated { get; set; }

        [JsonIgnore]
        public ConcurrentDictionary<string, List<IndexEntry>> ChildrenByParent { get; set; }

        public class IndexEntry
        {
            public string Guid { get; set; }
            public string Name { get; set; }
            public string Type { get; set; }
            public string Description { get; set; }
            public string Parent { get; set; }
            public string ParentPath { get; set; }
            public string Path { get; set; }
            public string Module { get; set; }
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
            
            // Attribute specific
            public string DataType { get; set; }
            public int Length { get; set; }
            public int Decimals { get; set; }
            public bool IsFormula { get; set; }

            // Table/Transaction specific
            public string RootTable { get; set; }
            
            public string SourceSnippet { get; set; }
            public string FullSource { get; set; }
            public int Complexity { get; set; }
            public string ParmRule { get; set; }
            public float[] Embedding { get; set; }
        }

        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);
        public static SearchIndex FromJson(string json) => JsonConvert.DeserializeObject<SearchIndex>(json);
    }
}
