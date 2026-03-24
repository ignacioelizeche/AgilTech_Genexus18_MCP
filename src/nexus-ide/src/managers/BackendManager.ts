import * as vscode from "vscode";
import * as path from "path";
import * as fs from "fs";
import * as cp from "child_process";
import { GxFileSystemProvider } from "../gxFileSystem";
import { readJsonFile, resolveGatewayConfigPath, resolveGatewayHttpPort } from "../utils/GatewayConfig";
import { 
  CONFIG_SECTION, 
  CONFIG_AUTO_START, 
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
  public onRecovered: (() => Promise<void>) | undefined;
  private static readonly STARTUP_RETRIES = 20;
  private static readonly STARTUP_DELAY_MS = 1500;
  private readonly backendLogPath: string;

  constructor(private readonly context: vscode.ExtensionContext) {
    this.backendLogPath = path.join(
      this.context.extensionPath,
      "backend_manager_debug.log",
    );
  }

  async start(provider: GxFileSystemProvider, forceStart = false): Promise<boolean> {
    const config = vscode.workspace.getConfiguration(CONFIG_SECTION);
    const developmentBackendActive = this.hasDevelopmentGatewayAvailable();

    if (await this.isGatewayAlreadyReady(provider)) {
      console.log("[BackendManager] Reusing already running MCP Gateway.");
      this.trace(
        `Reusing already running MCP Gateway on port ${this.getEffectivePort(config)}.`,
      );
      this.ownsBackendProcess = false;
      this.healthMonitor = new BackendHealthMonitor(provider, this.context, this);
      this.healthMonitor.start();
      return true;
    }

    const autoStart = config.get(CONFIG_AUTO_START);
    if (!forceStart && !autoStart) {
      this.trace("Auto-start disabled and no ready gateway was detected.");
      return false;
    }

    const resolvedBackend = this.resolveBackendDirectory();
    let backendDir = resolvedBackend.backendDir;
    let gatewayExe = resolvedBackend.gatewayExe;

    if (developmentBackendActive) {
      this.killStaleGatewayProcesses();
    }

    const configFile = resolveGatewayConfigPath(this.context.extensionPath);

    if (!fs.existsSync(gatewayExe)) {
      this.trace(`Gateway executable not found: ${gatewayExe}`);
      vscode.window.showErrorMessage(
        "GeneXus MCP Gateway not found. Please build the project or check installation.",
      );
      return false;
    }

    let persistedConfig: any = undefined;
    if (fs.existsSync(configFile)) {
      try {
        persistedConfig = readJsonFile(configFile);
      } catch (e) {
        console.error("[BackendManager] Failed to read canonical config.json:", e);
      }
    }

    const kbPath =
      (await this.findBestKbPath()) ||
      persistedConfig?.Environment?.KBPath ||
      "";
    const installationPath =
      this.findBestInstallationPath() ||
      persistedConfig?.GeneXus?.InstallationPath ||
      "";

    if (!kbPath || !installationPath) {
      this.trace(
        `Auto-start aborted. kbPath='${kbPath}' installationPath='${installationPath}'`,
      );
      console.log(
        "[BackendManager] Missing KB Path or Installation Path. Auto-start aborted.",
      );
      return false;
    }

    if (fs.existsSync(configFile)) {
      try {
        const currentConfig = persistedConfig ?? readJsonFile(configFile);
        currentConfig.GeneXus = currentConfig.GeneXus || {};
        currentConfig.Environment = currentConfig.Environment || {};
        currentConfig.Server = currentConfig.Server || {};
        currentConfig.GeneXus.InstallationPath = installationPath;
        currentConfig.Environment.KBPath = kbPath;
        fs.writeFileSync(configFile, JSON.stringify(currentConfig, null, 2));
      } catch (e) {
        console.error("[BackendManager] Failed to update canonical config.json:", e);
      }
    }

    console.log("[BackendManager] Starting MCP Gateway...");
    this.trace(`Starting MCP Gateway. backendDir='${backendDir}' configFile='${configFile}' effectivePort='${effectivePortPreview(config)}'`);
    try {
      const effectivePort = this.getEffectivePort(config);
      const launchSpec = this.resolveLaunchSpec(backendDir);
      console.log(
        `[BackendManager] Launch command: ${launchSpec.command} ${launchSpec.args.join(" ")}`.trim(),
      );
      this.trace(
        `Launch command: ${launchSpec.command} ${launchSpec.args.join(" ")}`.trim(),
      );

      if (developmentBackendActive) {
        this.launchDevelopmentGateway(configFile, effectivePort);

        this.backendProcess = undefined;
        this.ownsBackendProcess = false;
      } else {
        this.backendProcess = cp.spawn(launchSpec.command, launchSpec.args, {
          cwd: backendDir,
          detached: false,
          stdio: ["pipe", "ignore", "ignore"],
          windowsHide: true,
          env: {
            ...process.env,
            GX_CONFIG_PATH: configFile,
            GX_MCP_PORT: String(effectivePort),
            GX_MCP_STDIO: "false",
          },
        });
        this.ownsBackendProcess = true;
      }
      this.trace(`Spawned gateway PID=${this.backendProcess?.pid ?? "unknown"}`);

      if (this.backendProcess) {
        this.backendProcess.on("error", (error) => {
          this.trace(`Gateway spawn error: ${error.message}`);
          console.error("[BackendManager] Gateway spawn failed:", error);
        });

        this.backendProcess.on("exit", (code) => {
          this.trace(`Gateway process exit. code=${code ?? "null"}`);
          console.log(`[BackendManager] Gateway exited with code ${code}`);
          this.backendProcess = undefined;
        });
      }
    } catch (e) {
      this.trace(`Failed to spawn gateway: ${String(e)}`);
      console.error("[BackendManager] Failed to spawn Gateway:", e);
    }

    await this.waitForGatewayReady(provider);
    this.trace("waitForGatewayReady completed successfully.");

    this.healthMonitor = new BackendHealthMonitor(provider, this.context, this);
    this.healthMonitor.start();
    return true;
  }

  stop() {
    this.trace(
      `stop() called. ownsBackendProcess=${this.ownsBackendProcess} pid=${this.backendProcess?.pid ?? "none"}`,
    );
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

  private getEffectivePort(config: vscode.WorkspaceConfiguration): number {
    return resolveGatewayHttpPort(this.context.extensionPath, config);
  }

  async restart(provider: GxFileSystemProvider, forceStart = false) {
    this.stop();
    await this.start(provider, forceStart);
    if (this.onRecovered) {
      try { await this.onRecovered(); }
      catch (e) { console.error("[BackendManager] onRecovered callback failed:", e); }
    }
  }

  private resolveLaunchSpec(backendDir: string): { command: string; args: string[] } {
    const gatewayDll = path.join(backendDir, "GxMcp.Gateway.dll");
    const gatewayExe = path.join(backendDir, "GxMcp.Gateway.exe");

    if (fs.existsSync(gatewayExe)) {
      return {
        command: gatewayExe,
        args: [],
      };
    }

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

  private resolveBackendDirectory(): { backendDir: string; gatewayExe: string } {
    const packagedBackendDir = path.join(this.context.extensionPath, "backend");
    const packagedGatewayExe = path.join(packagedBackendDir, "GxMcp.Gateway.exe");

    const devGatewayDir = path.join(
      this.context.extensionPath,
      "..",
      "GxMcp.Gateway",
      "bin",
      "Debug",
      "net8.0-windows",
    );
    const devGatewayExe = path.join(devGatewayDir, "GxMcp.Gateway.exe");

    if (fs.existsSync(devGatewayExe)) {
      console.log(`[BackendManager] Using development gateway at: ${devGatewayDir}`);
      return {
        backendDir: devGatewayDir,
        gatewayExe: devGatewayExe,
      };
    }

    const publishDir = path.join(this.context.extensionPath, "..", "..", "publish");
    const publishGatewayExe = path.join(publishDir, "GxMcp.Gateway.exe");
    if (fs.existsSync(publishGatewayExe)) {
      console.log(`[BackendManager] Using development publish backend at: ${publishDir}`);
      return {
        backendDir: publishDir,
        gatewayExe: publishGatewayExe,
      };
    }

    return {
      backendDir: packagedBackendDir,
      gatewayExe: packagedGatewayExe,
    };
  }

  private hasDevelopmentGatewayAvailable(): boolean {
    const devGatewayExe = path.join(
      this.context.extensionPath,
      "..",
      "GxMcp.Gateway",
      "bin",
      "Debug",
      "net8.0-windows",
      "GxMcp.Gateway.exe",
    );

    return fs.existsSync(devGatewayExe);
  }

  private launchDevelopmentGateway(configFile: string, effectivePort: number): void {
    const bootstrapScript = path.join(
      this.context.extensionPath,
      "start-debug-gateway.ps1",
    );

    if (!fs.existsSync(bootstrapScript)) {
      throw new Error(`Debug gateway bootstrap script not found: ${bootstrapScript}`);
    }

    const launchResult = cp.spawnSync(
      "powershell.exe",
      [
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        bootstrapScript,
        "-ConfigPath",
        configFile,
        "-Port",
        String(effectivePort),
      ],
      {
        cwd: this.context.extensionPath,
        stdio: "pipe",
        encoding: "utf8",
        windowsHide: true,
        env: {
          ...process.env,
          GX_CONFIG_PATH: configFile,
          GX_MCP_PORT: String(effectivePort),
          GX_MCP_STDIO: "false",
        },
      },
    );

    const stdout = launchResult.stdout?.trim();
    const stderr = launchResult.stderr?.trim();

    if (stdout) {
      this.trace(`Debug bootstrap stdout: ${stdout}`);
    }

    if (stderr) {
      this.trace(`Debug bootstrap stderr: ${stderr}`);
    }

    if (launchResult.error) {
      throw launchResult.error;
    }

    if (launchResult.status !== 0) {
      throw new Error(
        `Debug gateway bootstrap failed with exit code ${launchResult.status}. ${stderr || stdout || ""}`.trim(),
      );
    }

    this.trace("Development gateway launched via start-debug-gateway.ps1.");
  }

  private killStaleGatewayProcesses(): void {
    try {
      const windowsKillScript = [
        "taskkill /F /IM GxMcp.Gateway.exe /T 2>$null",
        "taskkill /F /IM GxMcp.Worker.exe /T 2>$null",
        "$gatewayDotnet = Get-CimInstance Win32_Process | Where-Object { $_.Name -ieq 'dotnet.exe' -and $_.CommandLine -match 'GxMcp\\.Gateway\\.dll' }",
        "foreach ($proc in $gatewayDotnet) { try { Stop-Process -Id $proc.ProcessId -Force -ErrorAction Stop } catch {} }",
        "$gatewayWrappers = Get-CimInstance Win32_Process | Where-Object { $_.Name -ieq 'powershell.exe' -and $_.CommandLine -match 'debug-gateway-wrapper\\.ps1' }",
        "foreach ($proc in $gatewayWrappers) { try { Stop-Process -Id $proc.ProcessId -Force -ErrorAction Stop } catch {} }",
      ].join("; ");

      cp.spawnSync("powershell.exe", ["-NoProfile", "-Command", windowsKillScript], {
        stdio: "ignore",
      });
      this.trace("Development startup cleanup completed.");
      console.log("[BackendManager] Development startup cleanup completed.");
    } catch (error) {
      this.trace(`Failed to kill stale gateway processes: ${String(error)}`);
      console.error("[BackendManager] Failed to kill stale gateway processes:", error);
    }
  }

  private async waitForGatewayReady(provider: GxFileSystemProvider): Promise<void> {
    let lastError: unknown;

    for (let attempt = 1; attempt <= BackendManager.STARTUP_RETRIES; attempt++) {
      try {
        const status = await provider.callMcpMethod("ping", undefined, 2000);
        if (status) {
          this.trace(`Gateway ready after ${attempt} attempt(s).`);
          console.log(
            `[BackendManager] Gateway ready after ${attempt} attempt(s).`,
          );
          return;
        }
      } catch (e) {
        lastError = e;
        this.trace(`Gateway not ready on attempt ${attempt}: ${String(e)}`);
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
      const status = await provider.callMcpMethod("ping", undefined, 5000);
      return Boolean(status);
    } catch {
      return false;
    }
  }

  private trace(message: string): void {
    try {
      fs.appendFileSync(
        this.backendLogPath,
        `[${new Date().toISOString()}] ${message}\n`,
      );
    } catch {}
  }
}

function effectivePortPreview(config: vscode.WorkspaceConfiguration): number {
  return resolveGatewayHttpPort(
    path.resolve(__dirname, "..", ".."),
    config,
  );
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
