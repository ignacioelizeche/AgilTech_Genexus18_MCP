using Newtonsoft.Json.Linq;
using System.Collections.Generic;

namespace GxMcp.Gateway.Routers
{
    public class SearchRouter : IMcpModuleRouter
    {
        public string ModuleName => "Search";

        public object ConvertToolCall(string toolName, JObject args)
        {
            switch (toolName)
            {
                case "genexus_query":
                case "genexus_list_objects":
                case "genexus_search":
                    string q = args?["query"]?.ToString() ?? args?["filter"]?.ToString() ?? "";
                    return new { module = "Search", action = "Query", target = q, limit = args?["limit"]?.ToObject<int?>() ?? 50 };
                default:
                    return null;
            }
        }
    }
}
