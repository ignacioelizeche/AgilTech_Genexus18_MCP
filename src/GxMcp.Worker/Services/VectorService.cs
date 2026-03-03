using System;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Worker.Services
{
    public class VectorService
    {
        // Simple TF-IDF inspired local embedding for GeneXus objects
        // This is a zero-dependency semantic bridge.
        
        public float[] ComputeEmbedding(string text)
        {
            if (string.IsNullOrEmpty(text)) return new float[128];
            
            // We use a fixed-size hashing vector for local comparison (SimHash-like)
            float[] vector = new float[128];
            var words = text.ToLower().Split(new[] { ' ', '.', ',', '(', ')', '[', ']', ':', ';' }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var word in words)
            {
                int hash = word.GetHashCode();
                for (int i = 0; i < 128; i++)
                {
                    if (((hash >> (i % 32)) & 1) == 1)
                        vector[i] += 1.0f;
                    else
                        vector[i] -= 1.0f;
                }
            }

            // Normalize
            float magnitude = (float)Math.Sqrt(vector.Sum(v => v * v));
            if (magnitude > 0)
            {
                for (int i = 0; i < 128; i++) vector[i] /= magnitude;
            }
            
            return vector;
        }

        public float CosineSimilarity(float[] v1, float[] v2)
        {
            if (v1 == null || v2 == null || v1.Length != v2.Length) return 0;
            float dotProduct = 0;
            for (int i = 0; i < v1.Length; i++) dotProduct += v1[i] * v2[i];
            return dotProduct; // Already normalized vectors
        }
    }
}
