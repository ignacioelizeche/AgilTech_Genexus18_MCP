using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Routers
{
    public interface IMcpModuleRouter
    {
        string ModuleName { get; }
        object? ConvertToolCall(string toolName, JObject? arguments);
    }
}
