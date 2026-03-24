import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import { CONFIG_MCP_PORT, DEFAULT_MCP_PORT } from "../constants";

export function readJsonFile(filePath: string): any {
  const raw = fs.readFileSync(filePath, "utf8");
  const normalized = raw.charCodeAt(0) === 0xfeff ? raw.slice(1) : raw;
  return JSON.parse(normalized);
}

export function resolveGatewayConfigPath(extensionPath: string): string {
  const candidates = [
    path.resolve(extensionPath, "..", "..", "config.json"),
    path.join(extensionPath, "backend", "config.json"),
  ];

  for (const candidate of candidates) {
    if (fs.existsSync(candidate)) {
      return candidate;
    }
  }

  return candidates[0];
}

export function tryReadGatewayConfig(extensionPath: string): any | undefined {
  const configPath = resolveGatewayConfigPath(extensionPath);
  if (!fs.existsSync(configPath)) {
    return undefined;
  }

  try {
    return readJsonFile(configPath);
  } catch {
    return undefined;
  }
}

export function resolveGatewayHttpPort(
  extensionPath: string,
  workspaceConfig?: vscode.WorkspaceConfiguration,
): number {
  if (workspaceConfig) {
    const configuredPort = workspaceConfig.inspect<number>(CONFIG_MCP_PORT);
    const hasExplicitPort =
      configuredPort?.workspaceValue !== undefined ||
      configuredPort?.workspaceFolderValue !== undefined ||
      configuredPort?.globalValue !== undefined;

    if (hasExplicitPort) {
      return workspaceConfig.get(CONFIG_MCP_PORT, DEFAULT_MCP_PORT);
    }
  }

  const config = tryReadGatewayConfig(extensionPath);
  const canonicalPort = config?.Server?.HttpPort;
  return Number.isInteger(canonicalPort) && canonicalPort > 0
    ? canonicalPort
    : DEFAULT_MCP_PORT;
}
