using System;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class McpHttpProtocolTests
    {
        [Fact]
        public void TryApplyProtocol_ShouldSetSupportedVersionWhenHeaderIsMissing()
        {
            var context = new DefaultHttpContext();

            var error = McpHttpProtocol.TryApplyProtocol(context.Request, context.Response.Headers);

            Assert.Null(error);
            Assert.Equal(McpRouter.SupportedProtocolVersion, context.Response.Headers["MCP-Protocol-Version"].ToString());
        }

        [Fact]
        public void TryApplyProtocol_ShouldRejectUnsupportedVersion()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["MCP-Protocol-Version"] = "2025-03-26";

            var error = McpHttpProtocol.TryApplyProtocol(context.Request, context.Response.Headers);

            Assert.NotNull(error);
            Assert.Equal(StatusCodes.Status400BadRequest, error.Value.StatusCode);
            Assert.Contains("Unsupported MCP protocol version", error.Value.Message);
        }

        [Fact]
        public void TryApplyProtocol_ShouldAcceptSupportedVersion()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["MCP-Protocol-Version"] = McpRouter.SupportedProtocolVersion;

            var error = McpHttpProtocol.TryApplyProtocol(context.Request, context.Response.Headers);

            Assert.Null(error);
            Assert.Equal(McpRouter.SupportedProtocolVersion, context.Response.Headers["MCP-Protocol-Version"].ToString());
        }

        [Fact]
        public void TryGetValidSession_ShouldAllowInitializeWithoutSessionHeader()
        {
            var registry = new HttpSessionRegistry(TimeSpan.FromMinutes(5));
            var context = new DefaultHttpContext();
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":"init","method":"initialize"}""");

            var error = McpHttpProtocol.TryGetValidSession(registry, context.Request, request, out var session);

            Assert.Null(error);
            Assert.Null(session);
        }

        [Fact]
        public void TryGetValidSession_ShouldRequireSessionHeaderForNonInitializeCalls()
        {
            var registry = new HttpSessionRegistry(TimeSpan.FromMinutes(5));
            var context = new DefaultHttpContext();
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":"1","method":"tools/list"}""");

            var error = McpHttpProtocol.TryGetValidSession(registry, context.Request, request, out var session);

            Assert.NotNull(error);
            Assert.Equal(StatusCodes.Status400BadRequest, error.Value.StatusCode);
            Assert.Equal("Missing MCP-Session-Id header.", error.Value.Message);
            Assert.Null(session);
        }

        [Fact]
        public void TryGetValidSession_ShouldRejectExpiredSession()
        {
            var registry = new HttpSessionRegistry(TimeSpan.FromMilliseconds(1));
            var session = registry.Create();
            var context = new DefaultHttpContext();
            context.Request.Headers["MCP-Session-Id"] = session.Id;
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":"1","method":"tools/list"}""");

            System.Threading.Thread.Sleep(20);

            var error = McpHttpProtocol.TryGetValidSession(registry, context.Request, request, out var resolved);

            Assert.NotNull(error);
            Assert.Equal(StatusCodes.Status404NotFound, error.Value.StatusCode);
            Assert.Equal("Unknown or expired MCP session.", error.Value.Message);
            Assert.Null(resolved);
        }
    }
}
