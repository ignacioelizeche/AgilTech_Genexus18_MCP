import * as vscode from "vscode";
import * as path from "path";
import * as fs from "fs";
import * as cp from "child_process";
import { GxFileSystemProvider } from "../gxFileSystem";

export class BackendManager {
  private backendProcess: cp.ChildProcess | undefined;
  private healthMonitor: BackendHealthMonitor | undefined;

  constructor(private readonly context: vscode.ExtensionContext) {}

  async start(provider: GxFileSystemProvider) {
    const config = vscode.workspace.getConfiguration();
    const autoStart = config.get("genexus.autoStartBackend");
    if (!autoStart) return;

    let backendDir = path.join(this.context.extensionPath, "backend");
    let gatewayExe = path.join(backendDir, "GxMcp.Gateway.exe");

    // Development Fallback: Check project root publish folder
    if (!fs.existsSync(gatewayExe)) {
      const devPath = path.join(this.context.extensionPath, "..", "..", "publish");
      if (fs.existsSync(path.join(devPath, "GxMcp.Gateway.exe"))) {
        backendDir = devPath;
        gatewayExe = path.join(backendDir, "GxMcp.Gateway.exe");
        console.log(`[BackendManager] Using Development backend at: ${backendDir}`);
      }
    }

    const configFile = path.join(backendDir, "config.json");

    if (!fs.existsSync(gatewayExe)) {
      vscode.window.showErrorMessage(
        "GeneXus MCP Gateway not found. Please build the project or check installation.",
      );
      return;
    }

    const kbPath = await this.findBestKbPath();
    const installationPath = this.findBestInstallationPath();

    if (!kbPath || !installationPath) {
      console.log(
        "[BackendManager] Missing KB Path or Installation Path. Auto-start aborted.",
      );
      return;
    }

    if (fs.existsSync(configFile)) {
      try {
        const currentConfig = JSON.parse(fs.readFileSync(configFile, "utf8"));
        currentConfig.GeneXus.InstallationPath = installationPath;
        currentConfig.Environment.KBPath = kbPath;
        currentConfig.Server.HttpPort = config.get("genexus.mcpPort", 5000);
        fs.writeFileSync(configFile, JSON.stringify(currentConfig, null, 2));
      } catch (e) {
        console.error("[BackendManager] Failed to update config.json:", e);
      }
    }

    console.log("[BackendManager] Starting MCP Gateway...");
    try {
      this.backendProcess = cp.spawn(gatewayExe, [], {
        cwd: backendDir,
        detached: false,
        stdio: "ignore",
      });

      this.backendProcess.on("exit", (code) => {
        console.log(`[BackendManager] Gateway exited with code ${code}`);
        this.backendProcess = undefined;
      });
    } catch (e) {
      console.error("[BackendManager] Failed to spawn Gateway:", e);
    }

    this.healthMonitor = new BackendHealthMonitor(provider, this.context, this);
    this.healthMonitor.start();
  }

  stop() {
    this.healthMonitor?.stop();
    if (this.backendProcess) {
      this.backendProcess.kill();
      this.backendProcess = undefined;
    }
  }

  private async findBestKbPath(): Promise<string> {
    const config = vscode.workspace.getConfiguration();
    let kbPath = config.get<string>("genexus.kbPath", "");

    if (kbPath && fs.existsSync(kbPath)) {
      return kbPath;
    }

    try {
      console.log("[BackendManager] Searching for .gxw files...");
      const files = await vscode.workspace.findFiles(
        "*.gxw",
        "**/node_modules/**",
        1,
      );
      console.log(
        `[BackendManager] findFiles returned ${files.length} results.`,
      );
      if (files.length > 0) {
        const found = path.dirname(files[0].fsPath);
        console.log(`[BackendManager] Found KB at: ${found}`);
        return found;
      }
    } catch (e) {
      console.error("[BackendManager] Error in findFiles:", e);
    }

    // Use configuration or empty string, no hardcoded defaults
    return "";
  }

  private findBestInstallationPath(): string {
    const config = vscode.workspace.getConfiguration();
    const currentPath = config.get<string>("genexus.installationPath", "");
    return currentPath;
  }

  async restart(provider: GxFileSystemProvider) {
    this.stop();
    await this.start(provider);
  }
}

class BackendHealthMonitor {
  private _interval: NodeJS.Timeout | undefined;
  private _consecutiveFailures = 0;
  private _isRestarting = false;

  constructor(
    private readonly provider: GxFileSystemProvider,
    private readonly context: vscode.ExtensionContext,
    private readonly manager: BackendManager,
  ) {}

  start() {
    if (this._interval) return;
    this._interval = setInterval(() => this.check(), 10000);
  }

  async check() {
    if (this._isRestarting) return;

    const isIndexing = this.provider.isBulkIndexing;
    const timeout = isIndexing ? 15000 : 5000;

    try {
      const status = await this.provider.callGateway(
        {
          method: "execute_command",
          params: { module: "Health", action: "Ping" },
        },
        timeout,
      );
      if (status) {
        this._consecutiveFailures = 0;
      } else {
        throw new Error("No response");
      }
    } catch (e) {
      if (isIndexing) return;

      this._consecutiveFailures++;
      if (this._consecutiveFailures >= 3) {
        this.showWarning();
      }
    }
  }

  private async showWarning() {
    const selection = await vscode.window.showWarningMessage(
      "GeneXus MCP Server parou de responder.",
      "Restart Services",
      "Wait",
    );

    if (selection === "Restart Services") {
      this._isRestarting = true;
      await this.manager.restart(this.provider);
      this._isRestarting = false;
      this._consecutiveFailures = 0;
    } else {
      this._consecutiveFailures = 0;
    }
  }

  stop() {
    if (this._interval) clearInterval(this._interval);
  }
}
