import * as vscode from "vscode";
import * as fs from "fs";
import * as path from "path";
import { GxFileSystemProvider } from "./gxFileSystem";
import { GxTreeProvider } from "./gxTreeProvider";
import { GxActionsProvider } from "./gxActionsProvider";
import { GxDiagnosticProvider } from "./diagnosticProvider";
import { GxShadowService } from "./gxShadowService";

import { BackendManager } from "./managers/BackendManager";
import { ShadowManager } from "./managers/ShadowManager";
import { ContextManager } from "./managers/ContextManager";
import { CommandManager } from "./managers/CommandManager";
import { ProviderManager } from "./managers/ProviderManager";
import { McpDiscoveryManager } from "./managers/McpDiscoveryManager";
import { SyncManager } from "./managers/SyncManager";
import { GxUriParser } from "./utils/GxUriParser";
import { resolveGatewayHttpPort, tryReadGatewayConfig } from "./utils/GatewayConfig";
import { 
  GX_SCHEME, 
  STATE_KEY_FOLDER_ADDED, 
  VIEW_EXPLORER, 
  VIEW_ACTIONS,
  CONFIG_SECTION,
  DEFAULT_STATUS_BAR_TIMEOUT,
  ROOT_PARENT_NAME,
} from "./constants";

let backendManager: BackendManager;
const IS_TEST_MODE = process.env.NEXUS_IDE_TEST_MODE === "1";
let isActivated = false;
let activeShadowRoot: string | undefined;
let pendingMountPromise: Promise<void> | undefined;
let bootstrapOutput: vscode.OutputChannel | undefined;
let lifecycleLogPath: string | undefined;

type BootstrapReporter = (message: string) => void;

function getBootstrapOutput(): vscode.OutputChannel {
  if (!bootstrapOutput) {
    bootstrapOutput = vscode.window.createOutputChannel("GeneXus MCP Bootstrap");
  }

  return bootstrapOutput;
}

function reportBootstrapStatus(message: string, report?: BootstrapReporter): void {
  const formatted = `[Nexus IDE] ${message}`;
  console.log(formatted);
  getBootstrapOutput().appendLine(formatted);
  appendLifecycleLog(formatted);
  report?.(message);
}

function appendLifecycleLog(message: string): void {
  if (!lifecycleLogPath) {
    return;
  }

  try {
    fs.appendFileSync(
      lifecycleLogPath,
      `[${new Date().toISOString()}] ${message}\n`,
    );
  } catch {}
}

function isMirrorReadyForMount(shadowRoot: string): boolean {
  if (!fs.existsSync(shadowRoot)) {
    return false;
  }

  const indexPath = path.join(shadowRoot, ".gx_index.json");
  if (!fs.existsSync(indexPath)) {
    return false;
  }

  try {
    const raw = fs.readFileSync(indexPath, "utf8");
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) && parsed.length > 0;
  } catch {
    return false;
  }
}

function getErrorMessage(error: unknown): string {
  if (error instanceof Error) {
    return error.message;
  }

  return String(error ?? "");
}

function isSearchIndexUnavailable(error: unknown): boolean {
  const message = getErrorMessage(error).toLowerCase();
  return message.includes("index missing") || message.includes("index empty");
}

function isRootBrowseEmpty(error: unknown): boolean {
  const message = getErrorMessage(error).toLowerCase();
  return message.includes("root browse returned 0 objects");
}

function isGatewayTimeout(error: unknown): boolean {
  const message = getErrorMessage(error).toLowerCase();
  return message.includes("timeout gateway");
}

function isGatewayConnectionRefused(error: unknown): boolean {
  const message = getErrorMessage(error).toLowerCase();
  return message.includes("connect econnrefused");
}

function containsReplacementCharacter(text: string | undefined): boolean {
  return typeof text === "string" && text.includes("\uFFFD");
}

async function readIndexStatus(provider: GxFileSystemProvider): Promise<any> {
  const status = await provider.callMcpTool(
    "genexus_lifecycle",
    { action: "status", _ts: Date.now() },
    60000,
  );

  if (typeof status?.error === "string" && status.error.length > 0) {
    throw new Error(status.error);
  }

  return status;
}

async function rebuildSearchIndex(
  provider: GxFileSystemProvider,
  report?: BootstrapReporter,
): Promise<void> {
  reportBootstrapStatus("Starting KB reindex...", report);
  vscode.window.setStatusBarMessage(
    "$(sync~spin) GeneXus: Reconstruindo indice da KB...",
    5000,
  );

  const startResult = await provider.callMcpTool(
    "genexus_lifecycle",
    { action: "index", _ts: Date.now() },
    300000,
  );

  if (typeof startResult?.error === "string" && startResult.error.length > 0) {
    throw new Error(startResult.error);
  }

  const timeoutAt = Date.now() + 15 * 60 * 1000;
  while (Date.now() < timeoutAt) {
    await new Promise((resolve) => setTimeout(resolve, 1500));
    const status = await readIndexStatus(provider);

    const processed = Number(status?.processed ?? 0);
    const total = Number(status?.total ?? 0);
    const statusText =
      typeof status?.status === "string" ? status.status : "";
    const progressMessage =
      `Indexando KB ${processed}/${total}${statusText ? ` - ${statusText}` : ""}`;

    vscode.window.setStatusBarMessage(
      `$(sync~spin) GeneXus: ${progressMessage}`,
      2000,
    );
    reportBootstrapStatus(progressMessage, report);

    if (status?.isIndexing === true) {
      continue;
    }

    if (
      total > 0 &&
      statusText.toLowerCase() !== "error" &&
      (statusText.toLowerCase() === "complete" ||
        processed >= total ||
        processed > 0)
    ) {
      reportBootstrapStatus("Search index completed.", report);
      return;
    }
  }

  throw new Error("Timed out waiting for KB reindex.");
}

async function ensureSearchIndexReady(
  provider: GxFileSystemProvider,
  report?: BootstrapReporter,
): Promise<void> {
  let initialStatus: any;
  try {
    initialStatus = await readIndexStatus(provider);
  } catch (error) {
    if (isGatewayTimeout(error)) {
      reportBootstrapStatus(
        "Index status request timed out. Proceeding directly to materialization queries.",
        report,
      );
      return;
    }
    throw error;
  }
  const initialProcessed = Number(initialStatus?.processed ?? 0);
  const initialTotal = Number(initialStatus?.total ?? 0);
  const initialStatusText =
    typeof initialStatus?.status === "string" ? initialStatus.status : "";

  if (
    initialStatus?.isIndexing !== true &&
    initialTotal > 0 &&
    initialStatusText.toLowerCase() !== "error"
  ) {
    reportBootstrapStatus(
      `Search index already available (${initialProcessed}/${initialTotal}${initialStatusText ? ` - ${initialStatusText}` : ""}).`,
      report,
    );
    return;
  }

  if (
    initialStatus?.isIndexing !== true &&
    initialTotal === 0 &&
    initialProcessed === 0 &&
    initialStatusText.toLowerCase() !== "complete"
  ) {
    reportBootstrapStatus(
      "Search index unavailable during startup: lifecycle status reported no indexed objects.",
      report,
    );
    vscode.window.setStatusBarMessage(
      "$(sync~spin) GeneXus: Construindo índice inicial da KB...",
      5000,
    );

    report?.("Construindo indice inicial da KB...");
    await rebuildSearchIndex(provider, report);
    return;
  }

  await rebuildSearchIndex(provider, report);
}

async function mountMaterializedMirror(
  context: vscode.ExtensionContext,
  provider: GxFileSystemProvider,
  shadowRoot: string,
): Promise<void> {
  if (!pendingMountPromise) {
    pendingMountPromise = addKbFolder(context, 5, 2000, provider, shadowRoot)
      .finally(() => {
        pendingMountPromise = undefined;
      });
  }

  await pendingMountPromise;
}

export function activate(context: vscode.ExtensionContext) {
  if (isActivated) {
    console.log("[Nexus IDE] Activation skipped; extension already initialized.");
    return;
  }
  isActivated = true;

  console.log("[Nexus IDE] Extension activating...");
  lifecycleLogPath = path.join(context.extensionPath, "extension_lifecycle.log");
  appendLifecycleLog("[Nexus IDE] activate()");

  const provider = new GxFileSystemProvider();
  context.subscriptions.push(getBootstrapOutput());

  // 1. REGISTER COMMANDS FIRST (Ensure they are always available)
  context.subscriptions.push(
    vscode.commands.registerCommand("nexus-ide.openKb", () => {
      console.log("[Nexus IDE] Command 'nexus-ide.openKb' triggered.");
      addKbFolder(context, 5, 2000, provider);
    }),
    vscode.commands.registerCommand("nexus-ide.addKbFolder", () => {
      console.log("[Nexus IDE] Manual 'nexus-ide.addKbFolder' triggered.");
      addKbFolder(context, 5, 2000, provider);
    }),
    vscode.commands.registerCommand("nexus-ide.refreshFilesystem", () => {
      console.log(
        "[Nexus IDE] Command 'nexus-ide.refreshFilesystem' triggered.",
      );
      provider.clearDirCache();
    }),
  );

  // 2. REGISTER FILESYSTEM PROVIDER
  try {
    context.subscriptions.push(
      vscode.workspace.registerFileSystemProvider(GX_SCHEME, provider, {
        isCaseSensitive: false,
        isReadonly: false,
      }),
    );
    console.log(
      `[Nexus IDE] GxFileSystemProvider registered for scheme '${GX_SCHEME}'.`,
    );

    // Warm up
    vscode.workspace.fs
      .stat(vscode.Uri.from({ scheme: GX_SCHEME, path: "/" }))
      .then(
        () => console.log(`[Nexus IDE] Scheme '${GX_SCHEME}' warm up success.`),
        () => {},
      );
  } catch (e) {
    console.error("[Nexus IDE] FS registration failed:", e);
  }

  // 3. DEFERRED INITIALIZATION
  setImmediate(async () => {
    try {
      await initializeExtension(context, provider);
      console.log("[Nexus IDE] Initialization complete.");
    } catch (e) {
      console.error("[Nexus IDE] Init error:", e);
      if (e instanceof Error) {
        console.error(`[Nexus IDE] Stack: ${e.stack}`);
      }
    }
  });

  // Auto-add folder will now happen inside initializeExtension or on command
}

function resolveShadowRootPath(context: vscode.ExtensionContext): string {
  const config = tryReadGatewayConfig(context.extensionPath);
  const configured = config?.Environment?.GX_SHADOW_PATH;
  if (typeof configured === "string" && configured.trim().length > 0) {
    return configured;
  }

  return path.join(context.extensionPath, ".gx_mirror");
}

export async function addKbFolder(
  context: vscode.ExtensionContext,
  maxRetries = 5,
  delayMs = 2000,
  provider?: any,
  shadowRoot?: string,
) {
  const folders = vscode.workspace.workspaceFolders || [];
  const targetShadowRoot = shadowRoot || activeShadowRoot || resolveShadowRootPath(context);
  const mirrorReady = isMirrorReadyForMount(targetShadowRoot);

  if (!mirrorReady) {
    reportBootstrapStatus("Mirror is not ready for mount yet. Skipping workspace add.");
    vscode.window.setStatusBarMessage(
      "$(sync~spin) GeneXus: aguardando materializacao da KB...",
      DEFAULT_STATUS_BAR_TIMEOUT,
    );
    return;
  }

  const shadowUri = vscode.Uri.file(targetShadowRoot);
  const hasShadowFolder = folders.some((f) => f.uri.scheme === "file" && f.uri.fsPath === targetShadowRoot);
  if (hasShadowFolder) {
    reportBootstrapStatus("Mirror workspace already mounted. Forcing refresh.");

    try {
      provider?.clearDirCache?.();
      await vscode.commands.executeCommand("nexus-ide.refreshTree");
    } catch (e) {
      console.warn("[Nexus IDE] Failed to refresh mounted mirror folder:", e);
      getBootstrapOutput().appendLine(
        `[Nexus IDE] Failed to refresh mounted mirror folder: ${getErrorMessage(e)}`,
      );
    }

    try {
      await vscode.commands.executeCommand(`${VIEW_EXPLORER}.focus`);
    } catch {}

    vscode.window.setStatusBarMessage(
      "$(folder-opened) GeneXus KB pronta no Explorer",
      DEFAULT_STATUS_BAR_TIMEOUT,
    );
    return;
  }

  if (!hasShadowFolder) {
    reportBootstrapStatus("Checking if mirror workspace is ready for auto-mount...");

    for (let attempt = 1; attempt <= maxRetries; attempt++) {
      try {
        if (!fs.existsSync(targetShadowRoot)) {
          fs.mkdirSync(targetShadowRoot, { recursive: true });
        }

        reportBootstrapStatus(`Adding workspace folder: ${targetShadowRoot} on attempt ${attempt}`);
        vscode.workspace.updateWorkspaceFolders(folders.length, 0, {
          uri: shadowUri,
          name: "GeneXus KB",
        });
        context.globalState.update(STATE_KEY_FOLDER_ADDED, true);
        try {
          await vscode.commands.executeCommand(`${VIEW_EXPLORER}.focus`);
        } catch {}
        vscode.window.setStatusBarMessage(
          "$(folder-opened) GeneXus KB montada no Explorer",
          DEFAULT_STATUS_BAR_TIMEOUT,
        );
        return; // Success, exit retry loop
      } catch (e) {
        console.warn(
          `[Nexus IDE] Mirror mount point not ready yet (Attempt ${attempt}/${maxRetries}). Retrying in ${delayMs}ms...`,
        );
        if (attempt < maxRetries) {
          await new Promise(resolve => setTimeout(resolve, delayMs));
        } else {
          console.error("[Nexus IDE] Auto-mount failed after maximum retries.");
          vscode.window.showWarningMessage("Failed to connect to GeneXus KB MCP Server. You can try reconnecting manually from the Command Palette.");
        }
      }
    }
  }
}
function initializeExtension(
  context: vscode.ExtensionContext,
  provider: GxFileSystemProvider,
) {
  console.log("[Nexus IDE] Starting deferred initialization...");
  appendLifecycleLog("[Nexus IDE] initializeExtension()");

  const config = vscode.workspace.getConfiguration(CONFIG_SECTION);
  const port = resolveGatewayHttpPort(context.extensionPath, config);
  console.log(`[Nexus IDE] Using MCP port ${port}.`);
  provider.baseUrl = `http://127.0.0.1:${port}/mcp`;
  activeShadowRoot = resolveShadowRootPath(context);

  // Initialize Managers
  backendManager = new BackendManager(context);
  const contextManager = new ContextManager(context, provider);
  const shadowService = new GxShadowService(provider.baseUrl, activeShadowRoot);
  provider.setShadowService(shadowService);

  const discoveryManager = new McpDiscoveryManager(context, provider);
  discoveryManager.cleanupDevelopmentDiscoveryFiles();
  // Defer discovery registration until AFTER backend is potentially ready
  // discoveryManager.register(); // Moved below

  const diagnosticProvider = new GxDiagnosticProvider(provider);
  provider.setDiagnosticProvider(diagnosticProvider);

  const treeProvider = new GxTreeProvider(
    shadowService.shadowRoot,
    context.extensionUri,
  );

  const shadowManager = new ShadowManager(
    context,
    provider,
    shadowService,
    diagnosticProvider,
  );
  shadowManager.register();

  // UI Components
  const treeView = vscode.window.createTreeView(VIEW_EXPLORER, {
    treeDataProvider: treeProvider,
    showCollapseAll: true,
  });
  context.subscriptions.push(treeView);

  treeView.onDidChangeSelection((e) => {
    if (e.selection.length > 0) {
      const item: any = e.selection[0];
      if (item && item.resourceUri) {
        provider.preWarm(item.resourceUri);
      }
    }
  });

  const actionsProvider = new GxActionsProvider();
  vscode.window.createTreeView(VIEW_ACTIONS, {
    treeDataProvider: actionsProvider,
    showCollapseAll: false,
  });

  const providerManager = new ProviderManager(context, provider);
  providerManager.register();

  const commandManager = new CommandManager(
    context,
    provider,
    treeProvider,
    diagnosticProvider,
    contextManager,
    providerManager.historyProvider,
  );
  commandManager.register();

  contextManager.register();
  diagnosticProvider.subscribeToEvents(context);

  let pendingBackendRecovery: Promise<boolean> | undefined;
  const pendingMirrorHydrations = new Map<string, Promise<void>>();
  const recoverBackend = async (
    reason: string,
    report?: BootstrapReporter,
  ): Promise<boolean> => {
    if (!pendingBackendRecovery) {
      pendingBackendRecovery = (async () => {
        reportBootstrapStatus(
          `Backend unavailable during ${reason}. Attempting recovery...`,
          report,
        );
        const started = await backendManager.start(provider, true);
        if (!started) {
          reportBootstrapStatus(
            `Backend recovery could not start during ${reason}.`,
            report,
          );
          return false;
        }

        reportBootstrapStatus(
          `Backend recovery completed for ${reason}.`,
          report,
        );
        return true;
      })().finally(() => {
        pendingBackendRecovery = undefined;
      });
    }

    return pendingBackendRecovery;
  };

  // Recovery callback: re-materialize workspace when backend restarts
  backendManager.onRecovered = async () => {
    console.log("[Nexus IDE] Backend recovered. Re-materializing workspace...");
    try {
      await provider.initKb();
      if (!shadowService.hasMaterializedWorkspace()) {
        provider.isWorkspaceHydrating = true;
        await shadowService.materializeWorkspace(provider);
        provider.isWorkspaceHydrating = false;
      }
      treeProvider.refresh();
      provider.clearDirCache();
      vscode.window.setStatusBarMessage(
        "$(check) GeneXus: Workspace recuperado",
        DEFAULT_STATUS_BAR_TIMEOUT,
      );
    } catch (e) {
      provider.isWorkspaceHydrating = false;
      console.error("[Nexus IDE] Re-materialization after recovery failed:", e);
    }
  };

  // Start Backend and register discovery tools
  backendManager
    .start(provider)
    .then(async (started) => {
      if (!started) {
        console.warn("[Nexus IDE] Backend startup was skipped or aborted.");
        return;
      }
      if (IS_TEST_MODE) {
        console.log("[Nexus IDE] Test mode: skipping live MCP discovery registration.");
        return;
      }
      if (context.extensionMode === vscode.ExtensionMode.Development) {
        console.log(
          "[Nexus IDE] Backend started successfully. Skipping discovery registration in development mode.",
        );
      } else {
        console.log(
          "[Nexus IDE] Backend started successfully. Registering discovery tools...",
        );
        discoveryManager.register();
      }

      // KB Initialization (Unified Flow) - Moved INSIDE .then to ensure backend is READY
      console.log("[Nexus IDE] Initiating KB Initialization...");
      try {
        await provider.initKb();
        console.log("[Nexus IDE] KB background init complete.");

        // Phase 1: Materialization (critical)
        try {
          console.log("[Nexus IDE] Phase 1: Checking mirror status...");
          shadowService.consolidateLegacyMirrorFiles();
          let mirrorReady = shadowService.hasMaterializedWorkspace();
          const shadowDirExists = fs.existsSync(shadowService.shadowRoot);
          
          console.log(`[Nexus IDE] Mirror status: exists=${shadowDirExists}, ready=${mirrorReady}, path=${shadowService.shadowRoot}`);

            if (shadowDirExists && mirrorReady) {
              console.log("[Nexus IDE] Mirror already exists and is valid. Refreshing views...");
              treeProvider.refresh();
              provider.clearDirCache();
              await mountMaterializedMirror(context, provider, shadowService.shadowRoot);
              provider.warmMetadataAfterBootstrap();
              vscode.window.setStatusBarMessage(
                "$(check) GeneXus: Mirror pronto",
                DEFAULT_STATUS_BAR_TIMEOUT,
              );
          } else {
            // Final attempt at materialization with retry
            reportBootstrapStatus("Mirror missing or empty. Starting materialization...");
            provider.isWorkspaceHydrating = true;
            vscode.window.setStatusBarMessage(
              "$(sync~spin) GeneXus: Materializando workspace espelho...",
              5000,
            );
            const matSuccess = await vscode.window.withProgress(
              {
                location: vscode.ProgressLocation.Window,
                title: "GeneXus KB",
              },
              async (progress) => {
                let success = false;
                for (let attempt = 1; attempt <= 3; attempt++) {
                  const attemptReporter: BootstrapReporter = (message) => {
                    progress.report({ message: `Tentativa ${attempt}/3: ${message}` });
                  };
                  try {
                    reportBootstrapStatus(`Materialization attempt ${attempt}/3...`, attemptReporter);
                    if (attempt === 1) {
                      shadowService.resetMirrorWorkspace();
                      reportBootstrapStatus("Cleared stale mirror contents before materialization.", attemptReporter);
                    }
                    await ensureSearchIndexReady(provider, attemptReporter);
                    progress.report({ message: `Tentativa ${attempt}/3: Montando estrutura do mirror...` });
                    await shadowService.materializeWorkspaceWithProgress(provider, (state) => {
                      const currentParent =
                        state.currentParent === ROOT_PARENT_NAME ? "KB root" : state.currentParent;
                      const progressMessage =
                        `Tentativa ${attempt}/3: ${state.directoriesCreated} pastas, ${state.filesPrepared} arquivos, atual: ${currentParent}`;
                      progress.report({ message: progressMessage });
                      reportBootstrapStatus(
                        `Materializing mirror: directories=${state.directoriesCreated}, files=${state.filesPrepared}, current=${currentParent}`,
                      );
                    });
                    success = true;
                    reportBootstrapStatus("Materialization successful.", attemptReporter);
                    break;
                  } catch (e) {
                    console.error(`[Nexus IDE] Materialization attempt ${attempt} failed:`, e);
                    reportBootstrapStatus(
                      `Materialization attempt ${attempt}/3 failed: ${getErrorMessage(e)}`,
                      (message) => progress.report({ message }),
                    );
                    if (isRootBrowseEmpty(e)) {
                      reportBootstrapStatus(
                        "Materialization detected an inconsistent root hierarchy. Forcing a KB reindex before retrying.",
                        (message) => progress.report({ message }),
                      );
                      await rebuildSearchIndex(provider, attemptReporter);
                      continue;
                    }
                    if (isGatewayConnectionRefused(e)) {
                      const recovered = await recoverBackend(
                        "materialization",
                        (message) => progress.report({ message }),
                      );
                      if (recovered) {
                        continue;
                      }
                    }
                    if (attempt < 3) {
                      await new Promise((r) => setTimeout(r, 4000));
                    }
                  }
                }

                return success;
              },
            );

              if (matSuccess) {
                 reportBootstrapStatus("Finalizing materialization: refreshing tree and cache.");
                 treeProvider.refresh();
                 provider.clearDirCache();
                 await mountMaterializedMirror(context, provider, shadowService.shadowRoot);
                 provider.warmMetadataAfterBootstrap();
                  vscode.window.setStatusBarMessage(
                    "$(check) GeneXus: Workspace espelho pronto",
                    DEFAULT_STATUS_BAR_TIMEOUT,
                  );
            } else {
                throw new Error("Maximum materialization attempts reached.");
            }
          }
        } catch (matError) {
          console.error("[Nexus IDE] Materialization failed:", matError);
          vscode.window.showWarningMessage(
            "GeneXus: Falha ao materializar workspace. Tente 'GeneXus: Open KB' manualmente.",
          );
        } finally {
          provider.isWorkspaceHydrating = false;
        }
      } catch (kbError) {
        console.error("[Nexus IDE] KB init failed:", kbError);
      }
    })
    .catch((e) => console.error("[Nexus IDE] Backend start failed:", e));

  // Watch for Save event

  const hydrateMirrorDocument = async (doc: vscode.TextDocument): Promise<void> => {
    const hydrateKey = doc.uri.toString();
    const existingHydration = pendingMirrorHydrations.get(hydrateKey);
    if (existingHydration) {
      await existingHydration;
      return;
    }

    const runHydration = async (): Promise<void> => {
      if (!GxUriParser.isGeneXusUri(doc.uri) || doc.uri.scheme !== "file") {
        return;
      }

      const shouldHydrate =
        doc.getText().startsWith("// GXMCP_PLACEHOLDER:") ||
        shadowService.isPlaceholder(doc.uri.fsPath) ||
        containsReplacementCharacter(doc.getText());

      if (!shouldHydrate) {
        return;
      }

      console.log(`[Nexus IDE] Hydrating mirror file: ${doc.uri.fsPath}`);
      provider.beginInteractiveHydration();

      let hydrated = false;
      let hydrationError: unknown;
      try {
        hydrated = await shadowService.hydrateOpenedFile(
          doc.uri,
          provider,
          doc.getText(),
        );
      } catch (error) {
        hydrationError = error;
        if (isGatewayConnectionRefused(error)) {
          try {
            const recovered = await recoverBackend("file hydration");
            if (recovered) {
              hydrated = await shadowService.hydrateOpenedFile(
                doc.uri,
                provider,
                doc.getText(),
              );
            }
          } catch (recoveryError) {
            console.error(
              `[Nexus IDE] Backend recovery failed for ${doc.uri.fsPath}:`,
              recoveryError,
            );
            return;
          }
        } else {
          console.error(`[Nexus IDE] Hydration failed for ${doc.uri.fsPath}:`, error);
          return;
        }
      } finally {
        provider.endInteractiveHydration();
      }
      if (!hydrated && shadowService.isPlaceholder(doc.uri.fsPath)) {
        console.error(
          `[Nexus IDE] Hydration failed for ${doc.uri.fsPath}:`,
          hydrationError ?? "hydrate returned false",
        );
      }
      if (!hydrated || doc.isDirty) {
        return;
      }

      let hydratedText: string;
      try {
        hydratedText = fs.readFileSync(doc.uri.fsPath, "utf8");
      } catch (error) {
        console.error("[Nexus IDE] Failed to read hydrated mirror file:", error);
        return;
      }

      if (
        !hydratedText ||
        hydratedText.startsWith("// GXMCP_PLACEHOLDER:") ||
        hydratedText === doc.getText()
      ) {
        return;
      }

      provider.fireFileChange(doc.uri);
      const activeEditor = vscode.window.activeTextEditor;
      if (activeEditor?.document.uri.toString() === doc.uri.toString()) {
        try {
          await vscode.commands.executeCommand("workbench.action.files.revert");
        } catch (error) {
          console.error(
            `[Nexus IDE] Failed to revert hydrated mirror file ${doc.uri.fsPath}:`,
            error,
          );
        }
      }

      setTimeout(() => {
        const refreshedDoc = vscode.workspace.textDocuments.find(
          (candidate) => candidate.uri.toString() === doc.uri.toString(),
        );
        if (refreshedDoc) {
          void diagnosticProvider.refreshDiagnostics(refreshedDoc);
        }
      }, 2000);
    };

    const hydrationPromise = runHydration().finally(() => {
      pendingMirrorHydrations.delete(hydrateKey);
    });
    pendingMirrorHydrations.set(hydrateKey, hydrationPromise);
    await hydrationPromise;
  };

  context.subscriptions.push(
    vscode.workspace.onDidSaveTextDocument((doc: vscode.TextDocument) => {
      if (GxUriParser.isGeneXusUri(doc.uri)) {
        setTimeout(() => {
          diagnosticProvider.refreshAll();
        }, 1000);
      }
    }),
    vscode.workspace.onDidOpenTextDocument(async (doc: vscode.TextDocument) => {
      await hydrateMirrorDocument(doc);
    }),
    vscode.window.onDidChangeActiveTextEditor(async (editor) => {
      if (!editor) {
        return;
      }

      await hydrateMirrorDocument(editor.document);
    }),
  );

  const visibleMirrorDocs = Array.from(
    new Map(
      vscode.window.visibleTextEditors.map((editor) => [
        editor.document.uri.toString(),
        editor.document,
      ]),
    ).values(),
  );

  void Promise.all(
    visibleMirrorDocs.map((doc) => hydrateMirrorDocument(doc)),
  ).catch((error) => {
    console.error("[Nexus IDE] Initial mirror hydration failed:", error);
  });

  console.log("[Nexus IDE] Deferred initialization complete.");
}

export function deactivate() {
  appendLifecycleLog("[Nexus IDE] deactivate()");
  isActivated = false;
  if (backendManager) {
    backendManager.stop();
  }
}
