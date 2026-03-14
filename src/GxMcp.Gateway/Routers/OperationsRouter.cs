using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Routers
{
    public class OperationsRouter : IMcpModuleRouter
    {
        public string ModuleName => "Operations";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            switch (toolName)
            {
                case "genexus_create_object":
                    return new
                    {
                        module = "Object",
                        action = "Create",
                        target = args?["name"]?.ToString(),
                        type = args?["type"]?.ToString()
                    };

                case "genexus_refactor":
                    return ConvertRefactorToolCall(args);

                case "genexus_add_variable":
                    return new
                    {
                        module = "Write",
                        action = "AddVariable",
                        target = args?["name"]?.ToString(),
                        varName = args?["varName"]?.ToString(),
                        typeName = args?["typeName"]?.ToString()
                    };

                case "genexus_explain_code":
                    return new
                    {
                        module = "Analyze",
                        action = "ExplainCode",
                        target = args?["name"]?.ToString(),
                        payload = args?["code"]?.ToString()
                    };

                case "genexus_format":
                    return new
                    {
                        module = "Formatting",
                        action = "Format",
                        payload = args?["code"]?.ToString()
                    };

                case "genexus_properties":
                    return ConvertPropertiesToolCall(args);

                case "genexus_history":
                    return new
                    {
                        module = "History",
                        action = args?["action"]?.ToString(),
                        target = args?["name"]?.ToString(),
                        versionId = args?["versionId"]?.ToObject<int?>()
                    };

                case "genexus_structure":
                    return ConvertStructureToolCall(args);

                default:
                    return null;
            }
        }

        private static object? ConvertRefactorToolCall(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) return null;

            if (action == "ExtractProcedure")
            {
                return new
                {
                    module = "Refactor",
                    action,
                    target = args?["objectName"]?.ToString(),
                    payload = new JObject
                    {
                        ["code"] = args?["code"]?.ToString(),
                        ["procedureName"] = args?["procedureName"]?.ToString()
                    }.ToString()
                };
            }

            string? target = args?["target"]?.ToString();
            if (action == "RenameVariable")
            {
                target = args?["objectName"]?.ToString();
            }

            return new
            {
                module = "Refactor",
                action,
                target,
                payload = new JObject
                {
                    ["oldName"] = args?["target"]?.ToString(),
                    ["newName"] = args?["newName"]?.ToString()
                }.ToString()
            };
        }

        private static object? ConvertPropertiesToolCall(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) return null;

            if (action.Equals("set", System.StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    module = "Property",
                    action = "Set",
                    target = args?["name"]?.ToString(),
                    propertyName = args?["propertyName"]?.ToString(),
                    value = args?["value"]?.ToString(),
                    control = args?["control"]?.ToString()
                };
            }

            return new
            {
                module = "Property",
                action = "Get",
                target = args?["name"]?.ToString(),
                control = args?["control"]?.ToString()
            };
        }

        private static object? ConvertStructureToolCall(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            string? mappedAction = action switch
            {
                "get_visual" => "GetVisualStructure",
                "update_visual" => "UpdateVisualStructure",
                "get_indexes" => "GetVisualIndexes",
                "get_logic" => "GetLogicStructure",
                _ => null
            };

            if (mappedAction == null) return null;

            return new
            {
                module = "Structure",
                action = mappedAction,
                target = args?["name"]?.ToString(),
                payload = args?["payload"]?.Type == JTokenType.Object || args?["payload"]?.Type == JTokenType.Array
                    ? args?["payload"]?.ToString()
                    : args?["payload"]?.ToString()
            };
        }
    }
}
