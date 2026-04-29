using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Models
{
    public sealed class SemanticOp
    {
        public string Op { get; set; } = "";
        public JObject Args { get; set; } = new JObject();

        public static SemanticOp From(JObject raw)
        {
            string op = raw["op"]?.ToString();
            if (string.IsNullOrEmpty(op))
                throw new ArgumentException("op required");
            JObject args = (JObject)raw.DeepClone();
            args.Remove("op");
            return new SemanticOp { Op = op, Args = args };
        }
    }
}
