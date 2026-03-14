using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    internal readonly record struct McpHttpError(int StatusCode, string Message);

    internal static class McpHttpProtocol
    {
        public static bool IsInitializeRequest(JObject requestObj)
        {
            return string.Equals(requestObj["method"]?.ToString(), "initialize", StringComparison.Ordinal);
        }

        public static McpHttpError? TryApplyProtocol(HttpRequest request, IHeaderDictionary responseHeaders)
        {
            string? requestedProtocolVersion = request.Headers["MCP-Protocol-Version"].FirstOrDefault();
            if (!string.IsNullOrEmpty(requestedProtocolVersion) &&
                !string.Equals(requestedProtocolVersion, McpRouter.SupportedProtocolVersion, StringComparison.Ordinal))
            {
                return new McpHttpError(StatusCodes.Status400BadRequest,
                    $"Unsupported MCP protocol version '{requestedProtocolVersion}'. Expected '{McpRouter.SupportedProtocolVersion}'.");
            }

            responseHeaders["MCP-Protocol-Version"] = McpRouter.SupportedProtocolVersion;
            return null;
        }

        public static McpHttpError? TryGetValidSession(HttpSessionRegistry sessionRegistry, HttpRequest request, JObject requestObj, out HttpSessionState? session)
        {
            session = null;

            if (IsInitializeRequest(requestObj)) return null;

            string? sessionId = request.Headers["MCP-Session-Id"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return new McpHttpError(StatusCodes.Status400BadRequest, "Missing MCP-Session-Id header.");
            }

            if (!sessionRegistry.TryGet(sessionId, out session))
            {
                return new McpHttpError(StatusCodes.Status404NotFound, "Unknown or expired MCP session.");
            }

            return null;
        }
    }
}
