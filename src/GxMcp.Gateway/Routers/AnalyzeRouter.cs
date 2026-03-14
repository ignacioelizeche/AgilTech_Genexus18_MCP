using Newtonsoft.Json.Linq;
namespace GxMcp.Gateway.Routers
{
    public class AnalyzeRouter : IMcpModuleRouter
    {
        public string ModuleName => "Analyze";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            string? target = args?["name"]?.ToString();
            
            switch (toolName)
            {
                case "genexus_inspect":
                    // Por padrão, se não especificar, o GetConversionContext já traz quase tudo.
                    // Para manter a compatibilidade interna, usamos o module Analyze.
                    return new { module = "Analyze", action = "GetConversionContext", target = target, include = args?["include"] };

                case "genexus_summarize":
                    return new { module = "Analyze", action = "Summarize", target = target };
                
                case "genexus_get_sql":
                    return new { module = "Analyze", action = "GetSQL", target = target };

                case "genexus_inject_context":
                    bool recursive = args?["recursive"]?.Value<bool>() ?? false;
                    return new { module = "Analyze", action = "InjectContext", target = target, recursive = recursive };

                case "genexus_analyze":
                    string? mode = args?["mode"]?.ToString();
                    switch (mode)
                    {
                        case "linter":
                            return new { module = "Linter", action = "Analyze", target = target };
                        case "navigation":
                            return new { module = "Analyze", action = "GetNavigation", target = target };
                        case "hierarchy":
                            return new { module = "Analyze", action = "GetHierarchy", target = target };
                        case "impact":
                            return new { module = "Analyze", action = "Analyze", target = target };
                        case "data_context":
                            return new { module = "Analyze", action = "GetDataContext", target = target };
                        case "ui_context":
                            return new { module = "UI", action = "GetUIContext", target = target };
                        case "pattern_metadata":
                            return new { module = "Analyze", action = "GetPatternMetadata", target = target };
                        default:
                            return new { module = "Analyze", action = "Analyze", target = target };
                    }
                
                // Aliases legados (para compatibilidade se algo chamar via tool call)
                case "genexus_get_signature":
                    return new { module = "Analyze", action = "GetParameters", target = target };
                case "genexus_linter":
                    return new { module = "Linter", action = "Analyze", target = target };
                case "genexus_get_navigation":
                    return new { module = "Analyze", action = "GetNavigation", target = target };
                
                default:
                    return null;
            }
        }
    }
}
