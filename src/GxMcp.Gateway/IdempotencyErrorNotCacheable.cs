using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    public sealed class ErrorNotCacheable : Exception
    {
        public JObject Result { get; }
        public ErrorNotCacheable(JObject result) : base("Error result — not cacheable") { Result = result; }
    }
}
