using Newtonsoft.Json.Linq;
namespace GxMcp.Gateway.Routers
{
    public class AnalyzeRouter : IMcpModuleRouter
    {
        public string ModuleName => "Analyze";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            string? target = args?["name"]?.ToString();
            string? type = args?["type"]?.ToString();

            switch (toolName)
            {
                case "genexus_inspect":
                    return new { module = "Analyze", action = "GetConversionContext", target = target, include = args?["include"], type = type };

                case "genexus_summarize":
                    return new { module = "Analyze", action = "Summarize", target = target, type = type };

                case "genexus_get_sql":
                    return new { module = "Analyze", action = "GetSQL", target = target, type = type };

                case "genexus_inject_context":
                    bool recursive = args?["recursive"]?.Value<bool>() ?? false;
                    return new { module = "Analyze", action = "InjectContext", target = target, recursive = recursive, type = type };

                case "genexus_analyze":
                    string? mode = args?["mode"]?.ToString();
                    switch (mode)
                    {
                        case "linter":
                            return new { module = "Linter", action = "Analyze", target = target, type = type };
                        case "navigation":
                            return new { module = "Analyze", action = "GetNavigation", target = target, type = type };
                        case "hierarchy":
                            return new { module = "Analyze", action = "GetHierarchy", target = target, type = type };
                        case "impact":
                            return new { module = "Analyze", action = "Analyze", target = target, type = type };
                        case "data_context":
                            return new { module = "Analyze", action = "GetDataContext", target = target, type = type };
                        case "ui_context":
                            return new { module = "UI", action = "GetUIContext", target = target, type = type };
                        case "pattern_metadata":
                            return new { module = "Analyze", action = "GetPatternMetadata", target = target, type = type };
                        default:
                            return new { module = "Analyze", action = "Analyze", target = target, type = type };
                    }

                case "genexus_get_signature":
                    return new { module = "Analyze", action = "GetParameters", target = target, type = type };
                case "genexus_linter":
                    return new { module = "Linter", action = "Analyze", target = target, type = type };
                case "genexus_get_navigation":
                    return new { module = "Analyze", action = "GetNavigation", target = target, type = type };

                default:
                    return null;
            }
        }
    }
}
