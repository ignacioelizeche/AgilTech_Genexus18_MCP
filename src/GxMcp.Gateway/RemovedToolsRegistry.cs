using System;
using System.Collections.Generic;

namespace GxMcp.Gateway
{
    public sealed class RemovedToolInfo
    {
        public RemovedToolInfo(string replacedBy, string argHint)
        {
            ReplacedBy = replacedBy;
            ArgHint = argHint;
        }

        public string ReplacedBy { get; }
        public string ArgHint { get; }
    }

    public static class RemovedToolsRegistry
    {
        public static readonly IReadOnlyDictionary<string, RemovedToolInfo> Map =
            new Dictionary<string, RemovedToolInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["genexus_batch_read"] = new RemovedToolInfo("genexus_read", "use targets[] (array of {name, part})"),
                ["genexus_batch_edit"] = new RemovedToolInfo("genexus_edit", "use targets[] (array of edit requests, each {name, mode, content|ops|patch, ...})")
            };
    }
}
