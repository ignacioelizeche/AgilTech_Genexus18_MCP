import * as http from "http";
import { GxShadowService } from "../gxShadowService";
import { DEFAULT_MCP_PORT } from "../constants";

const MCP_PROTOCOL_VERSION = "2025-06-18";

export class GxGatewayClient {
  private _baseUrl = `http://127.0.0.1:${DEFAULT_MCP_PORT}/mcp`;
  private _mcpSessionId?: string;
  private _shadowService?: GxShadowService;

  constructor(baseUrl: string, shadowService?: GxShadowService) {
    this._baseUrl = baseUrl;
    this._shadowService = shadowService;
  }

  public get baseUrl(): string {
    return this._baseUrl;
  }

  set baseUrl(url: string) {
    this._baseUrl = url;
  }

  private get mcpBaseUrl(): string {
    if (this._baseUrl.endsWith("/mcp")) {
      return this._baseUrl;
    }
    return this._baseUrl;
  }

  async initializeMcpSession(customTimeout?: number): Promise<string> {
    if (this._mcpSessionId) {
      return this._mcpSessionId;
    }

    const response = await this.postRawJsonRpc(
      this.mcpBaseUrl,
      {
        jsonrpc: "2.0",
        id: "initialize",
        method: "initialize",
        params: {
          protocolVersion: MCP_PROTOCOL_VERSION,
          capabilities: {},
          clientInfo: {
            name: "nexus-ide",
            version: "1.0.0",
          },
        },
      },
      customTimeout,
      {
        "MCP-Protocol-Version": MCP_PROTOCOL_VERSION,
      },
    );

    const sessionId = response.headers["mcp-session-id"];
    if (!sessionId) {
      throw new Error("MCP session was not established by the gateway.");
    }

    this._mcpSessionId = Array.isArray(sessionId) ? sessionId[0] : sessionId;
    return this._mcpSessionId;
  }

  async callMcp(method: string, params?: any, customTimeout?: number): Promise<any> {
    const sessionId = await this.initializeMcpSession(customTimeout);
    const response = await this.postRawJsonRpc(
      this.mcpBaseUrl,
      {
        jsonrpc: "2.0",
        id: `${method}-${Date.now()}`,
        method,
        params,
      },
      customTimeout,
      {
        "MCP-Protocol-Version": MCP_PROTOCOL_VERSION,
        "MCP-Session-Id": sessionId,
      },
    );

    return this.unwrapGatewayResponse(response.body);
  }

  async listMcpTools(customTimeout?: number): Promise<any[]> {
    const result = await this.callMcp("tools/list", undefined, customTimeout);
    return Array.isArray(result?.tools) ? result.tools : [];
  }

  async listMcpResources(customTimeout?: number): Promise<any[]> {
    const result = await this.callMcp("resources/list", undefined, customTimeout);
    return Array.isArray(result?.resources) ? result.resources : [];
  }

  async listMcpResourceTemplates(customTimeout?: number): Promise<any[]> {
    const result = await this.callMcp(
      "resources/templates/list",
      undefined,
      customTimeout,
    );
    return Array.isArray(result?.resourceTemplates)
      ? result.resourceTemplates
      : [];
  }

  async listMcpPrompts(customTimeout?: number): Promise<any[]> {
    const result = await this.callMcp("prompts/list", undefined, customTimeout);
    return Array.isArray(result?.prompts) ? result.prompts : [];
  }

  async callMcpTool(name: string, args?: any, customTimeout?: number): Promise<any> {
    return this.callMcp(
      "tools/call",
      {
        name,
        arguments: args ?? {},
      },
      customTimeout,
    );
  }

  async readMcpResource(uri: string, customTimeout?: number): Promise<any> {
    return this.callMcp(
      "resources/read",
      {
        uri,
      },
      customTimeout,
    );
  }

  async getMcpPrompt(
    name: string,
    args?: any,
    customTimeout?: number,
  ): Promise<any> {
    return this.callMcp(
      "prompts/get",
      {
        name,
        arguments: args ?? {},
      },
      customTimeout,
    );
  }

  private unwrapGatewayResponse(body: string): any {
    try {
      console.log(`[GxGateway] Response body received (length: ${body.length})`);
      const fullResponse = JSON.parse(body);

      if (fullResponse && fullResponse.result) {
        const mcpResult = fullResponse.result;
        const blocks = Array.isArray(mcpResult.content)
          ? mcpResult.content
          : Array.isArray(mcpResult.contents)
            ? mcpResult.contents
            : null;
        if (blocks && blocks.length > 0) {
          const text = blocks[0].text;
          try {
            const trimmed = text.trim();
            if (trimmed.startsWith("{") || trimmed.startsWith("[")) {
              try {
                return JSON.parse(trimmed);
              } catch (innerE) {
                console.error(`[GxGateway] JSON parse error in content:`, innerE);
                return text;
              }
            }
            return text;
          } catch {
            return text;
          }
        }

        console.log(`[GxGateway] Found result wrapper but no content list.`);
        return fullResponse.result;
      }

      console.log(`[GxGateway] No result wrapper found.`);
      return fullResponse;
    } catch {
      return body;
    }
  }

  private async postRawJsonRpc(
    targetUrl: string,
    command: any,
    customTimeout?: number,
    extraHeaders?: Record<string, string>,
  ): Promise<{ body: string; headers: http.IncomingHttpHeaders }> {
    return new Promise((resolve, reject) => {
      if (this._shadowService && command.params) {
        command.params.shadowPath = this._shadowService.shadowRoot;
      }

      const data = JSON.stringify(command);
      const timeout = customTimeout || 60000;

      console.log(
        `[GxGateway] Calling: ${targetUrl} with module ${command.module ?? command.method}...`,
      );
      const url = new URL(targetUrl);
      const req = http.request(
        url,
        {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            "Content-Length": Buffer.byteLength(data),
            ...(extraHeaders ?? {}),
          },
          timeout: timeout,
        },
        (res) => {
          console.log(
            `[GxGateway] Response status: ${res.statusCode} for module: ${command.module ?? command.method}`,
          );
          let body = "";
          res.on("data", (chunk) => (body += chunk));
          res.on("end", () => {
            resolve({ body, headers: res.headers });
          });
        },
      );

      req.on("timeout", () => {
        req.destroy();
        reject(new Error(`Timeout Gateway (${timeout / 1000}s)`));
      });

      req.on("error", reject);
      req.write(data);
      req.end();
    });
  }
}
