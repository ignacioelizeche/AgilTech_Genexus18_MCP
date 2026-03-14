import * as vscode from "vscode";
import * as path from "path";
import * as fs from "fs";
import * as cp from "child_process";
import { GxFileSystemProvider } from "../gxFileSystem";
import { 
  CONFIG_SECTION, 
  CONFIG_AUTO_START, 
  CONFIG_MCP_PORT, 
  DEFAULT_MCP_PORT,
  CONFIG_KB_PATH,
  CONFIG_INSTALL_PATH,
  MODULE_HEALTH,
  HEALTH_CHECK_INTERVAL,
  HEALTH_CHECK_TIMEOUT,
  HEALTH_CHECK_TIMEOUT_INDEXING
} from "../constants";

export class BackendManager {
  private backendProcess: cp.ChildProcess | undefined;
  private healthMonitor: BackendHealthMonitor | undefined;
  private ownsBackendProcess = false;
  private static readonly STARTUP_RETRIES = 20;
  private static readonly STARTUP_DELAY_MS = 1500;

  constructor(private readonly context: vscode.ExtensionContext) {}

  async start(provider: GxFileSystemProvider) {
    const config = vscode.workspace.getConfiguration(CONFIG_SECTION);
    const autoStart = config.get(CONFIG_AUTO_START);
    if (!autoStart) return;

    if (await this.isGatewayAlreadyReady(provider)) {
      console.log("[BackendManager] Reusing already running MCP Gateway.");
      this.ownsBackendProcess = false;
      this.healthMonitor = new BackendHealthMonitor(provider, this.context, this);
      this.healthMonitor.start();
      return;
    }

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
        currentConfig.Server.HttpPort = config.get(CONFIG_MCP_PORT, DEFAULT_MCP_PORT);
        fs.writeFileSync(configFile, JSON.stringify(currentConfig, null, 2));
      } catch (e) {
        console.error("[BackendManager] Failed to update config.json:", e);
      }
    }

    console.log("[BackendManager] Starting MCP Gateway...");
    try {
      const launchSpec = this.resolveLaunchSpec(backendDir);
      console.log(
        `[BackendManager] Launch command: ${launchSpec.command} ${launchSpec.args.join(" ")}`.trim(),
      );

      this.backendProcess = cp.spawn(launchSpec.command, launchSpec.args, {
        cwd: backendDir,
        detached: false,
        stdio: "ignore",
      });
      this.ownsBackendProcess = true;

      this.backendProcess.on("error", (error) => {
        console.error("[BackendManager] Gateway spawn failed:", error);
      });

      this.backendProcess.on("exit", (code) => {
        console.log(`[BackendManager] Gateway exited with code ${code}`);
        this.backendProcess = undefined;
      });
    } catch (e) {
      console.error("[BackendManager] Failed to spawn Gateway:", e);
    }

    await this.waitForGatewayReady(provider);

    this.healthMonitor = new BackendHealthMonitor(provider, this.context, this);
    this.healthMonitor.start();
  }

  stop() {
    this.healthMonitor?.stop();
    if (this.backendProcess && this.ownsBackendProcess) {
      this.backendProcess.kill();
    }
    this.backendProcess = undefined;
    this.ownsBackendProcess = false;
  }

  private async findBestKbPath(): Promise<string> {
    const config = vscode.workspace.getConfiguration(CONFIG_SECTION);
    let kbPath = config.get<string>(CONFIG_KB_PATH, "");

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
    const config = vscode.workspace.getConfiguration(CONFIG_SECTION);
    const currentPath = config.get<string>(CONFIG_INSTALL_PATH, "");
    return currentPath;
  }

  async restart(provider: GxFileSystemProvider) {
    this.stop();
    await this.start(provider);
  }

  private resolveLaunchSpec(backendDir: string): { command: string; args: string[] } {
    const gatewayDll = path.join(backendDir, "GxMcp.Gateway.dll");
    const gatewayExe = path.join(backendDir, "GxMcp.Gateway.exe");

    if (fs.existsSync(gatewayDll)) {
      return {
        command: "dotnet",
        args: [gatewayDll],
      };
    }

    return {
      command: gatewayExe,
      args: [],
    };
  }

  private async waitForGatewayReady(provider: GxFileSystemProvider): Promise<void> {
    let lastError: unknown;

    for (let attempt = 1; attempt <= BackendManager.STARTUP_RETRIES; attempt++) {
      try {
        const status = await provider.callMcpMethod("ping", undefined, 2000);
        if (status) {
          console.log(
            `[BackendManager] Gateway ready after ${attempt} attempt(s).`,
          );
          return;
        }
      } catch (e) {
        lastError = e;
      }

      await new Promise((resolve) =>
        setTimeout(resolve, BackendManager.STARTUP_DELAY_MS),
      );
    }

    throw new Error(
      `Gateway did not become ready in time. Last error: ${lastError}`,
    );
  }

  private async isGatewayAlreadyReady(provider: GxFileSystemProvider): Promise<boolean> {
    try {
      const status = await provider.callMcpMethod("ping", undefined, 1500);
      return Boolean(status);
    } catch {
      return false;
    }
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
    this._interval = setInterval(() => this.check(), HEALTH_CHECK_INTERVAL);
  }

  async check() {
    if (this._isRestarting) return;

    const isIndexing = this.provider.isBulkIndexing;
    const timeout = isIndexing ? HEALTH_CHECK_TIMEOUT_INDEXING : HEALTH_CHECK_TIMEOUT;

    try {
      const status = await this.provider.callMcpMethod("ping", undefined, timeout);
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
