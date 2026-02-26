import * as vscode from "vscode";
import * as path from "path";
import * as fs from "fs";
import * as cp from "child_process";
import { GxFileSystemProvider } from "./gxFileSystem";
import { GxDocumentSymbolProvider } from "./symbolProvider";
import { GxTreeProvider, GxTreeItem } from "./gxTreeProvider";
import { GxDefinitionProvider } from "./definitionProvider";
import { GxHoverProvider } from "./hoverProvider";
import { GxCompletionItemProvider } from "./completionProvider";
import { GxInlineCompletionItemProvider } from "./inlineCompletionProvider";
import { GxDiagnosticProvider } from "./diagnosticProvider";
import { GxReferenceProvider } from "./referenceProvider";
import { GxWorkspaceSymbolProvider } from "./workspaceSymbolProvider";
import { GxCodeLensProvider } from "./codeLensProvider";
import { GxSignatureHelpProvider } from "./signatureHelpProvider";
import { GxCodeActionProvider } from "./codeActionProvider";
import { GxRenameProvider } from "./renameProvider";
import { GxFormatProvider } from "./formatProvider";
import { GxActionsProvider } from "./gxActionsProvider";
import { TYPE_SUFFIX } from "./gxFileSystem";
import { GxShadowService } from "./gxShadowService";

let backendProcess: cp.ChildProcess | undefined;

async function findBestKbPath(): Promise<string> {
  const config = vscode.workspace.getConfiguration();
  let kbPath = config.get<string>("genexus.kbPath", "");

  if (kbPath && fs.existsSync(kbPath)) {
    return kbPath;
  }

  // Auto-discovery: Look for .gxw files in workspace root or one level deep
  // PERFORMANCE: Do NOT use **/*.gxw because it will scan 100,000+ files in CSharpModel!
  const files = await vscode.workspace.findFiles(
    "{*.gxw,*/*.gxw}",
    "**/node_modules/**",
    1,
  );
  if (files.length > 0) {
    const foundPath = path.dirname(files[0].fsPath);
    console.log(`[Nexus IDE] Auto-discovered KB at: ${foundPath}`);
    return foundPath;
  }

  return "";
}

function findBestInstallationPath(): string {
  const config = vscode.workspace.getConfiguration();
  const currentPath = config.get<string>("genexus.installationPath", "");

  // If it's the default or empty, check if it actually exists
  if (
    !currentPath ||
    currentPath === "C:\\Program Files (x86)\\GeneXus\\GeneXus18"
  ) {
    const defaultPath = "C:\\Program Files (x86)\\GeneXus\\GeneXus18";
    if (fs.existsSync(path.join(defaultPath, "GeneXus.exe"))) {
      return defaultPath;
    }
  }

  return currentPath;
}

class BackendHealthMonitor {
  private _interval: NodeJS.Timeout | undefined;
  private _consecutiveFailures = 0;
  private _isRestarting = false;

  constructor(
    private readonly provider: GxFileSystemProvider,
    private readonly context: vscode.ExtensionContext,
  ) {}

  start() {
    if (this._interval) return;
    this._interval = setInterval(() => this.check(), 10000);
  }

  async check() {
    if (this._isRestarting) return;

    // Check if we are in bulk indexing (using a shared variable)
    const isIndexing = (this.context as any).isBulkIndexing || false;
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
      if (isIndexing) {
        console.log("[Nexus IDE] Slow ping during indexing. Ignoring.");
        return;
      }

      this._consecutiveFailures++;
      console.warn(
        `[Nexus IDE] Gateway heartbeat failed (${this._consecutiveFailures}/3)`,
      );
      if (this._consecutiveFailures >= 3) {
        this.showWarning();
      }
    }
  }

  private async showWarning() {
    const selection = await vscode.window.showWarningMessage(
      "GeneXus MCP Server parou de responder. O SDK pode estar sobrecarregado ou travado.",
      "Restart Services",
      "Wait",
    );

    if (selection === "Restart Services") {
      this.handleCrash();
    } else {
      // User chose to wait, reset counter but keep monitor running
      this._consecutiveFailures = 0;
    }
  }

  async handleCrash() {
    this._isRestarting = true;
    console.error(
      "[Nexus IDE] Gateway health check failed multiple times. Attempting restart...",
    );
    vscode.window.setStatusBarMessage(
      "$(error) GeneXus: Backend Offline. Tentando reiniciar...",
      5000,
    );

    try {
      if (backendProcess) {
        backendProcess.kill();
        backendProcess = undefined;
      }
      await startBackend(this.context);
      this._consecutiveFailures = 0;
    } catch (e) {
      console.error("[Nexus IDE] Auto-restart failed:", e);
    } finally {
      this._isRestarting = false;
    }
  }

  stop() {
    if (this._interval) clearInterval(this._interval);
  }
}

async function startBackend(context: vscode.ExtensionContext) {
  const config = vscode.workspace.getConfiguration();
  const autoStart = config.get("genexus.autoStartBackend");
  if (!autoStart) return;

  const backendDir = path.join(context.extensionPath, "backend");
  const gatewayExe = path.join(backendDir, "GxMcp.Gateway.exe");
  const configFile = path.join(backendDir, "config.json");

  if (!fs.existsSync(gatewayExe)) {
    console.log(
      "[Nexus IDE] Backend executable not found (Development Mode?). Skipping auto-start.",
    );
    return;
  }

  // Zero-Config: Auto-discover paths
  const kbPath = await findBestKbPath();
  const installationPath = findBestInstallationPath();

  if (!kbPath || !installationPath) {
    console.log(
      "[Nexus IDE] Missing KB Path or Installation Path. Auto-start aborted.",
    );
    return;
  }

  // Sync found paths to backend config.json
  if (fs.existsSync(configFile)) {
    try {
      const currentConfig = JSON.parse(fs.readFileSync(configFile, "utf8"));
      currentConfig.GeneXus.InstallationPath = installationPath;
      currentConfig.Environment.KBPath = kbPath;
      currentConfig.Server.HttpPort = config.get("genexus.mcpPort", 5000);
      fs.writeFileSync(configFile, JSON.stringify(currentConfig, null, 2));
    } catch (e) {
      console.error("[Nexus IDE] Failed to update config.json:", e);
    }
  }

  console.log("[Nexus IDE] Starting MCP Gateway...");
  try {
    backendProcess = cp.spawn(gatewayExe, [], {
      cwd: backendDir,
      detached: false,
      stdio: "ignore",
    });

    backendProcess.on("error", (err) => {
      console.error(`[Nexus IDE] Gateway Process Error: ${err.message}`);
    });

    backendProcess.on("exit", (code) => {
      console.log(`[Nexus IDE] Gateway exited with code ${code}`);
      backendProcess = undefined;
    });
  } catch (e) {
    console.error("[Nexus IDE] Failed to spawn Gateway:", e);
  }
}

export async function activate(context: vscode.ExtensionContext) {
  const config = vscode.workspace.getConfiguration();
  const provider = new GxFileSystemProvider();

  // Set dynamic port from config
  const port = config.get("genexus.mcpPort", 5000);
  provider.baseUrl = `http://localhost:${port}/api/command`;

  // Initialize Shadow Service (Invisible physical files for Gemini CLI)
  const shadowService = new GxShadowService(provider.baseUrl);
  provider.setShadowService(shadowService);

  let isBulkIndexing = false;
  let activeGxpItem: { uri: vscode.Uri; part: string } | null = null;
  let statusBarItem: vscode.StatusBarItem;

  // CRITICAL: Ensure shadow directory exists for Gemini CLI indexing
  const shadowRoot = shadowService.shadowRoot;
  if (!fs.existsSync(shadowRoot)) {
    fs.mkdirSync(shadowRoot, { recursive: true });
    console.log(`[Nexus IDE] Created shadow root: ${shadowRoot}`);
  }

  // Watcher for disk -> KB synchronization (Transparent to user)
  const shadowWatcher = vscode.workspace.createFileSystemWatcher(
    new vscode.RelativePattern(shadowService.shadowRoot, "**/*.gx"),
  );
  shadowWatcher.onDidChange(async (uri) => {
    if (isBulkIndexing) return; // Silent during bulk operations
    if (shadowService.shouldIgnore(uri.fsPath)) return; // Ignore self-writes

    // When Gemini CLI or user edits a shadow file, sync back to KB
    await shadowService.syncToKB(uri.fsPath);

    // Refresh diagnostics after sync
    await diagnosticProvider.refreshAll();
  });

  shadowWatcher.onDidCreate(async (uri) => {
    if (isBulkIndexing) return;
    if (shadowService.shouldIgnore(uri.fsPath)) return;
    await shadowService.syncToKB(uri.fsPath);
  });

  context.subscriptions.push(shadowWatcher);

  // 0. Start Backend in background (Non-blocking)
  startBackend(context).then(() => {
    const healthMonitor = new BackendHealthMonitor(provider, context);
    healthMonitor.start();
    context.subscriptions.push({ dispose: () => healthMonitor.stop() });

    // Share indexing state with monitor
    (context as any).isBulkIndexing = false;
  });

  // Custom Tree Provider for the GeneXus Explorer view (icons + ordering)
  const treeProvider = new GxTreeProvider(
    (cmd) => provider.callGateway(cmd),
    context.extensionUri,
  );
  const treeView = vscode.window.createTreeView("genexusExplorer", {
    treeDataProvider: treeProvider,
    showCollapseAll: true,
  });
  context.subscriptions.push(treeView);

  // PERFORMANCE: Pre-warm objects when clicked in the tree explorer
  treeView.onDidChangeSelection((e) => {
    if (e.selection.length > 0) {
      const item = e.selection[0];
      if (item.resourceUri) {
        provider.preWarm(item.resourceUri);
      }
    }
  });

  // Action Center (Elite Edition)
  const actionsProvider = new GxActionsProvider();
  vscode.window.createTreeView("genexusActions", {
    treeDataProvider: actionsProvider,
    showCollapseAll: false,
  });

  context.subscriptions.push(
    vscode.workspace.registerFileSystemProvider("genexus", provider, {
      isCaseSensitive: true,
      isReadonly: false,
    }),
  );

  // --- HISTORY DIFF PROVIDER ---
  const historyProvider = new (class
    implements vscode.TextDocumentContentProvider
  {
    private _data = new Map<string, string>();
    provideTextDocumentContent(uri: vscode.Uri): string {
      return this._data.get(uri.toString()) || "";
    }
    update(uri: vscode.Uri, content: string) {
      this._data.set(uri.toString(), content);
    }
    clear(uriPrefix: string) {
      for (const key of this._data.keys()) {
        if (key.includes(uriPrefix)) this._data.delete(key);
      }
    }
  })();
  context.subscriptions.push(
    vscode.workspace.registerTextDocumentContentProvider(
      "gx-history",
      historyProvider,
    ),
  );

  context.subscriptions.push(
    vscode.languages.registerDocumentSymbolProvider(
      "genexus",
      new GxDocumentSymbolProvider(),
    ),
  );
  context.subscriptions.push(
    vscode.languages.registerDefinitionProvider(
      "genexus",
      new GxDefinitionProvider((cmd) => provider.callGateway(cmd)),
    ),
  );
  context.subscriptions.push(
    vscode.languages.registerHoverProvider(
      "genexus",
      new GxHoverProvider((cmd) => provider.callGateway(cmd)),
    ),
  );
  context.subscriptions.push(
    vscode.languages.registerCompletionItemProvider(
      "genexus",
      new GxCompletionItemProvider((cmd) => provider.callGateway(cmd)),
      ".",
      "&",
    ),
  );
  context.subscriptions.push(
    vscode.languages.registerInlineCompletionItemProvider(
      "genexus",
      new GxInlineCompletionItemProvider(),
    ),
  );
  context.subscriptions.push(
    vscode.languages.registerSignatureHelpProvider(
      "genexus",
      new GxSignatureHelpProvider((cmd) => provider.callGateway(cmd)),
      "(",
      ",",
    ),
  );
  context.subscriptions.push(
    vscode.languages.registerCodeActionsProvider(
      "genexus",
      new GxCodeActionProvider((cmd) => provider.callGateway(cmd)),
      {
        providedCodeActionKinds: [GxCodeActionProvider.kind],
      },
    ),
  );
  context.subscriptions.push(
    vscode.languages.registerRenameProvider(
      "genexus",
      new GxRenameProvider((cmd) => provider.callGateway(cmd)),
    ),
  );
  context.subscriptions.push(
    vscode.languages.registerDocumentFormattingEditProvider(
      "genexus",
      new GxFormatProvider((cmd) => provider.callGateway(cmd)),
    ),
  );

  const diagnosticProvider = new GxDiagnosticProvider(
    (cmd) => provider.callGateway(cmd),
    provider,
  );
  provider.setDiagnosticProvider(diagnosticProvider);
  diagnosticProvider.subscribeToEvents(context);

  context.subscriptions.push(
    vscode.languages.registerWorkspaceSymbolProvider(
      new GxWorkspaceSymbolProvider((cmd) => provider.callGateway(cmd)),
    ),
  );
  context.subscriptions.push(
    vscode.languages.registerCodeLensProvider(
      "genexus",
      new GxCodeLensProvider((cmd) => provider.callGateway(cmd)),
    ),
  );
  context.subscriptions.push(
    vscode.languages.registerReferenceProvider(
      "genexus",
      new GxReferenceProvider((cmd) => provider.callGateway(cmd)),
    ),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand(
      "gx.showReferences",
      async (objName: string) => {
        const activeEditor = vscode.window.activeTextEditor;
        if (!activeEditor) return;

        // Trigger native reference peek/view at current position (top of file where CodeLens is)
        await vscode.commands.executeCommand(
          "editor.action.showReferences",
          activeEditor.document.uri,
          new vscode.Position(0, 0),
          [], // We let the provider find them
        );
      },
    ),
  );

  // --- INSTANT ACTIVATION ---
  // 1. Add virtual folder IMMEDIATELY (No delay, no await)
  const hasGxFolder = vscode.workspace.workspaceFolders?.some(
    (f) => f.uri.scheme === "genexus",
  );
  if (!hasGxFolder) {
    vscode.workspace.updateWorkspaceFolders(
      vscode.workspace.workspaceFolders
        ? vscode.workspace.workspaceFolders.length
        : 0,
      null,
      { uri: vscode.Uri.parse("genexus:/"), name: "GeneXus KB" },
    );
  }

  // 2. Fire and forget KB initialization + Transparent Bulk Indexing
  provider
    .initKb()
    .then(async () => {
      console.log("[Nexus IDE] KB background init complete.");
      treeProvider.refresh();

      // --- TRANSPARENT AUTO-INDEXING & SHADOWING ---
      // Se o shadowRoot estiver vazio ou for a primeira vez, dispara o indexamento em background
      const shadowPath = shadowService.shadowRoot;
      const shadowDirExists =
        fs.existsSync(shadowPath) && fs.readdirSync(shadowPath).length > 0;

      if (!shadowDirExists) {
        vscode.window.setStatusBarMessage(
          "$(sync~spin) GeneXus: Preparando ambiente (Index & Shadow)...",
          10000,
        );

        try {
          isBulkIndexing = true;
          (context as any).isBulkIndexing = true;
          // Dispara o BulkIndex (que agora também faz o Shadow Sync via Worker)
          await provider.callGateway({
            method: "execute_command",
            params: { module: "KB", action: "BulkIndex" },
          });

          // Monitoramento silencioso na barra de status
          let isDone = false;
          while (!isDone) {
            await new Promise((resolve) => setTimeout(resolve, 2000));
            const status = await provider.callGateway({
              method: "execute_command",
              params: { module: "KB", action: "GetIndexStatus" },
            });

            if (status && status.status === "Complete") {
              isDone = true;
              vscode.window.setStatusBarMessage(
                "$(check) GeneXus: Ambiente Pronto!",
                5000,
              );
            } else if (status && status.isIndexing) {
              vscode.window.setStatusBarMessage(
                `$(sync~spin) GeneXus: Indexando (${status.processed}/${status.total})...`,
                2000,
              );
            } else {
              isDone = true; // Parar se não estiver mais indexando por algum motivo
            }
          }
        } catch (e) {
          console.error("[Nexus IDE] Auto-index failed:", e);
        } finally {
          isBulkIndexing = false;
          (context as any).isBulkIndexing = false;
        }
      }
    })
    .catch((e) => {
      console.error("[Nexus IDE] Background init failed:", e);
    });

  const webviewPanels = new Map<string, vscode.WebviewPanel>();

  // Webview layout preview
  statusBarItem = vscode.window.createStatusBarItem(
    vscode.StatusBarAlignment.Right,
    100,
  );
  context.subscriptions.push(statusBarItem);

  const updateActiveContext = (uri?: vscode.Uri) => {
    if (uri && uri.scheme === "genexus") {
      const part = provider.getPart(uri);
      const path = decodeURIComponent(uri.path.substring(1));
      const objName = path.split("/").pop()!.replace(".gx", "");

      activeGxpItem = { uri, part };
      vscode.commands.executeCommand("setContext", "genexus.activePart", part);

      statusBarItem.text = `$(file-code) GX: ${objName} > ${part}`;
      statusBarItem.show();
    } else {
      activeGxpItem = null;
      vscode.commands.executeCommand("setContext", "genexus.activePart", null);
      statusBarItem.hide();
    }
  };

  context.subscriptions.push(
    vscode.window.onDidChangeActiveTextEditor((editor) => {
      updateActiveContext(editor?.document.uri);
    }),
  );

  // Initial update
  updateActiveContext(vscode.window.activeTextEditor?.document.uri);

  const showWebviewLayout = async (uri: vscode.Uri) => {
    const path = decodeURIComponent(uri.path.substring(1));
    const parts = path.split("/");
    const typeStr = parts.length > 1 ? parts[0] : null;
    const objName = parts.pop()!.replace(".gx", "");
    const uriKey = uri.toString();
    const target = typeStr ? `${typeStr}:${objName}` : objName;

    if (webviewPanels.has(uriKey)) {
      webviewPanels.get(uriKey)!.reveal(vscode.ViewColumn.Beside);
      return;
    }

    const panel = vscode.window.createWebviewPanel(
      "gxLayout",
      `${objName} Layout`,
      vscode.ViewColumn.Beside,
      { enableScripts: true, enableCommandUris: true },
    );

    webviewPanels.set(uriKey, panel);
    panel.onDidDispose(() => webviewPanels.delete(uriKey));

    panel.webview.html = "<h1>Carregando Layout...</h1>";

    try {
      const result = await provider.callGateway({
        method: "execute_command",
        params: {
          module: "Read",
          action: "ExtractSource",
          target: target,
          part: "Layout",
        },
      });
      if (result && result.source) {
        panel.webview.html = result.source;
      } else {
        panel.webview.html = "<h1>Erro ao carregar Layout</h1>";
      }
    } catch (e) {
      panel.webview.html = `<h1>Erro Crítico: ${e}</h1>`;
    }
  };

  // Visual Indexes Webview
  const showVisualIndexes = async (targetUri: vscode.Uri) => {
    const path = decodeURIComponent(targetUri.path.substring(1));
    const parts = path.split("/");
    const typeStr = parts.length > 1 ? parts[0] : null;
    const objName = parts.pop()!.replace(".gx", "");
    const uriKey = targetUri.toString() + ":VisualIndexes";
    const target = typeStr ? `${typeStr}:${objName}` : objName;

    if (webviewPanels.has(uriKey)) {
      webviewPanels.get(uriKey)!.reveal(vscode.ViewColumn.Beside);
      return;
    }

    const panel = vscode.window.createWebviewPanel(
      "gxVisualIndexes",
      `${objName} - Indexes`,
      vscode.ViewColumn.Beside,
      { enableScripts: true },
    );

    webviewPanels.set(uriKey, panel);
    panel.onDidDispose(() => webviewPanels.delete(uriKey));

    panel.webview.html = `
      <!DOCTYPE html>
      <html>
      <head>
        <style>
          :root {
            --bg: var(--vscode-editor-background);
            --fg: var(--vscode-editor-foreground);
            --header-bg: var(--vscode-editorWidget-background);
            --border: var(--vscode-panel-border);
            --hover-bg: var(--vscode-list-hoverBackground);
            --accent: var(--vscode-focusBorder);
          }
          body { font-family: var(--vscode-font-family); padding: 0; margin: 0; background-color: var(--bg); font-size: 13px; color: var(--fg); overflow: hidden; }
          .toolbar { background: var(--header-bg); padding: 10px 15px; border-bottom: 1px solid var(--border); font-weight: 600; }
          
          .table-container { overflow: auto; height: calc(100vh - 40px); }
          .tree-table { width: 100%; border-collapse: collapse; }
          .tree-table th { 
            text-align: left; background: var(--header-bg); padding: 10px; 
            border-bottom: 1px solid var(--border); border-right: 1px solid var(--border);
            font-weight: 600; position: sticky; top: 0; z-index: 10;
          }
          .tree-table td { padding: 10px; border-bottom: 1px solid var(--border); border-right: 1px solid var(--border); vertical-align: top; }
          .tree-table tr:hover { background-color: var(--hover-bg); }
          
          .index-name { font-weight: bold; display: flex; align-items: center; gap: 8px; }
          .primary-key { color: #d1b100; }
          .attr-list { margin: 0; padding: 0; list-style: none; }
          .attr-item { padding: 4px 0; border-bottom: 1px solid var(--border); display: flex; justify-content: space-between; }
          .attr-item:last-child { border-bottom: none; }
          .order-tag { font-size: 10px; opacity: 0.6; background: var(--hover-bg); padding: 1px 4px; border-radius: 2px; }
          .prop-tag { display: inline-block; font-size: 11px; padding: 2px 6px; border-radius: 10px; background: var(--accent); color: #fff; margin-right: 4px; }
        </style>
      </head>
      <body>
        <div class="toolbar">Indexes for ${objName}</div>
        <div class="table-container">
          <div id="content">Loading Indexes...</div>
        </div>

        <script>
          const vscode = acquireVsCodeApi();
          
          window.addEventListener('message', event => {
            const data = event.data;
            if (data.type === 'update') {
              renderIndexes(data.data);
            } else if (data.type === 'error') {
              document.getElementById('content').innerHTML = \`<h2 style="color:#f44; padding: 20px;">Error: \${data.message}</h2>\`;
            }
          });

          const icons = {
            key: \`<svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor"><path d="M11.5 1a3.49 3.49 0 0 0-3.3 2.33l-.1.3L1 11.06V15h3.94l1.37-1.37.13-.53-.53-.13L5 13.06V12h1v-1H5v-1h1V9h1v1h1.06l3.11-3.11.3-.1A3.5 3.5 0 1 0 11.5 1zm0 5a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3z"/></svg>\`,
            file: \`<svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor"><path fill-rule="evenodd" clip-rule="evenodd" d="M13.71 4.29l-3-3L10 1H4l-.5.5v13l.5.5h8l.5-.5V5l-.29-.71zM10 5V2.41L12.59 5H10zM4 14V2h5v4h4v8H4z"/></svg>\`
          };

          function renderIndexes(data) {
            if (!data.indexes || data.indexes.length === 0) {
              document.getElementById('content').innerHTML = '<p style="padding: 20px;">No indexes found.</p>';
              return;
            }

            let html = '<table class="tree-table"><thead><tr>';
            html += '<th style="width: 30%">Name</th><th style="width: 40%">Attributes</th><th>Properties</th>';
            html += '</tr></thead><tbody>';

            data.indexes.forEach(idx => {
              const rowClass = idx.isPrimary ? 'primary-key' : '';
              html += \`<tr>\`;
              html += \`<td class="index-name \${rowClass}">\${idx.isPrimary ? icons.key : icons.file} \${idx.name}</td>\`;
              
              html += '<td><ul class="attr-list">';
              idx.attributes.forEach(attr => {
                html += \`<li class="attr-item"><span>\${attr.name}</span> <span class="order-tag">\${attr.isAscending ? 'ASC' : 'DESC'}</span></li>\`;
              });
              html += '</ul></td>';

              html += \`<td>\`;
              if (idx.isPrimary) html += '<span class="prop-tag">Primary</span>';
              if (idx.isUnique) html += '<span class="prop-tag" style="background: #2da44e">Unique</span>';
              html += \`</td>\`;

              html += '</tr>';
            });

            html += '</tbody></table>';
            document.getElementById('content').innerHTML = html;
          }
        </script>
      </body>
      </html>
    `;

    try {
      const result = await provider.callGateway({
        method: "execute_command",
        params: {
          module: "Structure",
          action: "GetVisualIndexes",
          target: target,
        },
      });
      if (result && !result.error) {
        panel.webview.postMessage({ type: "update", data: result });
      } else {
        panel.webview.postMessage({
          type: "error",
          message: result?.error || "Unknown error",
        });
      }
    } catch (e) {
      panel.webview.postMessage({ type: "error", message: String(e) });
    }
  };

  // Visual Structure Webview
  const showVisualStructure = async (targetUri: vscode.Uri) => {
    const path = decodeURIComponent(targetUri.path.substring(1));
    const parts = path.split("/");
    const typeStr = parts.length > 1 ? parts[0] : null;
    const objName = parts.pop()!.replace(".gx", "");
    const uriKey = targetUri.toString() + ":VisualStructure";
    const target = typeStr ? `${typeStr}:${objName}` : objName;

    const isReadOnly = typeStr === "Table";

    if (webviewPanels.has(uriKey)) {
      webviewPanels.get(uriKey)!.reveal(vscode.ViewColumn.Beside);
      return;
    }

    const panel = vscode.window.createWebviewPanel(
      "gxVisualStructure",
      `${objName} - Structure`,
      vscode.ViewColumn.Beside,
      { enableScripts: true },
    );

    webviewPanels.set(uriKey, panel);
    panel.onDidDispose(() => webviewPanels.delete(uriKey));

    panel.webview.html = `
      <!DOCTYPE html>
      <html>
      <head>
        <style>
          :root {
            --bg: var(--vscode-editor-background);
            --fg: var(--vscode-editor-foreground);
            --header-bg: var(--vscode-editorWidget-background);
            --border: var(--vscode-panel-border);
            --hover-bg: var(--vscode-list-hoverBackground);
            --accent: var(--vscode-focusBorder);
            --input-bg: var(--vscode-input-background);
            --input-fg: var(--vscode-input-foreground);
            --tree-indent: 20px;
          }
          body { font-family: var(--vscode-font-family); padding: 0; margin: 0; background-color: var(--bg); font-size: 13px; color: var(--fg); overflow: hidden; }
          .toolbar { 
            background: var(--header-bg); padding: 8px 12px; border-bottom: 1px solid var(--border); 
            display: flex; gap: 8px; align-items: center; 
          }
          .toolbar button { 
            background: var(--vscode-button-secondaryBackground); color: var(--vscode-button-secondaryForeground);
            border: none; padding: 4px 10px; cursor: pointer; border-radius: 2px; 
            display: flex; align-items: center; gap: 6px; font-size: 12px;
          }
          .toolbar button:hover { background: var(--vscode-button-secondaryHoverBackground); }
          .toolbar button.primary { background: var(--vscode-button-background); color: var(--vscode-button-foreground); }
          .toolbar button.primary:hover { background: var(--vscode-button-hoverBackground); }
          
          .search-container { position: relative; flex: 1; max-width: 300px; }
          .search-input {
            width: 100%; background: var(--input-bg); color: var(--input-fg); border: 1px solid var(--border);
            padding: 4px 8px; border-radius: 2px; outline: none; font-size: 12px;
          }
          .search-input:focus { border-color: var(--accent); }

          .table-container { overflow: auto; height: calc(100vh - 42px); }
          .tree-table { width: 100%; border-collapse: collapse; table-layout: fixed; }
          .tree-table th { 
            text-align: left; background: var(--header-bg); padding: 8px 10px; 
            border-bottom: 1px solid var(--border); border-right: 1px solid var(--border);
            font-weight: 600; color: var(--fg); position: sticky; top: 0; z-index: 20; 
          }
          .tree-table td { 
            padding: 4px 10px; border-bottom: 1px solid var(--border); border-right: 1px solid var(--border);
            vertical-align: middle; white-space: nowrap; outline: none; overflow: hidden; text-overflow: ellipsis;
          }
          .tree-table tr:hover { background-color: var(--hover-bg); }
          .tree-table tr.selected { background-color: var(--vscode-list-activeSelectionBackground); color: var(--vscode-list-activeSelectionForeground); }

          .indent-guide {
            position: absolute; left: calc(var(--tree-indent) * var(--level) + 18px);
            top: 0; bottom: 0; width: 1px; background: var(--border); pointer-events: none;
          }
          
          .item-name-cell { display: flex; align-items: center; position: relative; }
          .indent { display: inline-block; width: var(--tree-indent); flex-shrink: 0; }
          .icon { margin-right: 6px; width: 16px; height: 16px; flex-shrink: 0; display: flex; align-items: center; justify-content: center; }
          
          .key-icon { color: #d1b100; cursor: pointer; }
          .attr-icon { color: #51a1ff; }
          .level-icon { color: #cccccc; }
          
          .level-row { font-weight: 600; background: var(--vscode-sideBar-background); }
          .formula-text { color: #4ec9b0; font-style: italic; }
          
          .actions-cell { width: 30px; text-align: center; border-right: none !important; }
          .btn-del { 
            cursor: pointer; color: var(--fg); opacity: 0.4; font-size: 16px; border: none; 
            background: transparent; padding: 2px; border-radius: 2px;
          }
          .btn-del:hover { opacity: 1; background: var(--vscode-toolbar-hoverBackground); color: #f44; }
          
          #status { margin-left: auto; font-size: 11px; opacity: 0.8; }
          #status.error { color: #f44; }
          #status.success { color: #4ec9b0; }
          
          .editable { cursor: text; transition: background 0.1s; }
          .editable:focus { background: var(--input-bg) !important; box-shadow: inset 0 0 0 1px var(--accent); }
          
          select.nullable-select { 
             width: 100%; border: none; background: transparent; font-family: inherit; font-size: inherit; color: inherit; appearance: none;
             cursor: pointer; outline: none;
          }
          .type-input {
            width: 100%; border: none; background: transparent; font-family: inherit; font-size: inherit; color: inherit; outline: none;
          }
          .readonly-badge { 
            background: var(--vscode-badge-background); color: var(--vscode-badge-foreground);
            padding: 2px 6px; border-radius: 10px; font-size: 10px; margin-left: 10px;
          }
        </style>
      </head>
      <body>
        <div class="toolbar">
          ${
            !isReadOnly
              ? `
            <button onclick="addRow(false)"><span>➕</span> Attr</button>
            <button onclick="addRow(true)"><span>📁</span> Level</button>
          `
              : `<span class="readonly-badge">READ-ONLY (Edit in Transaction)</span>`
          }
          <div class="search-container">
            <input type="text" class="search-input" placeholder="Filter..." oninput="filterRows(this.value)">
          </div>
          ${!isReadOnly ? `<button class="primary" onclick="requestSave()"><span>💾</span> Save</button>` : ""}
          <div id="status">Ready</div>
        </div>
        
        <datalist id="gx-types">
          <option value="NUMERIC"></option><option value="CHARACTER"></option><option value="VARCHAR"></option>
          <option value="LONGVARCHAR"></option><option value="DATE"></option><option value="DATETIME"></option>
          <option value="BOOLEAN"></option><option value="GUID"></option><option value="IMAGE"></option>
          <option value="BLOB"></option><option value="VIDEO"></option><option value="AUDIO"></option>
        </datalist>

        <div class="table-container">
          <div id="content">Loading Structure...</div>
        </div>

        <script>
          const vscode = acquireVsCodeApi();
          const isReadOnly = ${isReadOnly};
          let currentData = null;
          let filterText = "";
          
          function setStatus(text, type = '') {
            const el = document.getElementById('status');
            el.innerText = text;
            el.className = type;
          }

          window.addEventListener('message', event => {
            const data = event.data;
            if (data.type === 'update') {
              currentData = data.structure;
              renderStructure();
              setStatus('Loaded', 'success');
            } else if (data.type === 'error') {
              setStatus('Error', 'error');
              console.error('Error:', data.message);
            } else if (data.type === 'success') {
              setStatus('Saved', 'success');
            }
          });

          function filterRows(text) {
            filterText = text.toLowerCase();
            renderStructure();
          }

          const icons = {
            key: \`<svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor"><path d="M11.5 1a3.49 3.49 0 0 0-3.3 2.33l-.1.3L1 11.06V15h3.94l1.37-1.37.13-.53-.53-.13L5 13.06V12h1v-1H5v-1h1V9h1v1h1.06l3.11-3.11.3-.1A3.5 3.5 0 1 0 11.5 1zm0 5a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3z"/></svg>\`,
            attr: \`<svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor"><path d="M1 2v12h14V2H1zm13 11H2V3h12v10zM4 5h8v1H4V5zm0 3h8v1H4V8zm0 3h5v1H4v-1z"/></svg>\`,
            level: \`<svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor"><path d="M14.5 3H7.71l-2-2H1.5l-.5.5v11l.5.5h13l.5-.5v-9l-.5-.5zM14 12H2V2h3.29l2 2H14v8z"/></svg>\`
          };

          function renderStructure() {
            if (!currentData) return;
            let html = '<table class="tree-table"><thead><tr>';
            html += '<th style="width: 40%">Name</th><th style="width: 20%">Type</th><th style="width: 20%">Description</th><th style="width: 15%">Formula</th><th style="width: 80px">Null</th><th class="actions-cell"></th>';
            html += '</tr></thead><tbody>';
            
            function renderLevel(items, levelNum, parentId) {
              items.forEach((item, index) => {
                const id = (parentId ? parentId + '-' : '') + index;
                if (filterText && !item.name.toLowerCase().includes(filterText)) {
                   if (item.children) renderLevel(item.children, levelNum + 1, id);
                   return;
                }

                const indentSpace = '<span class="indent"></span>'.repeat(levelNum);
                const rowClass = item.isLevel ? 'level-row' : '';
                
                html += \`<tr class="\${rowClass}" data-id="\${id}">\`;
                
                // Name Column
                let iconHtml = item.isLevel ? icons.level : (item.isKey ? icons.key : icons.attr);
                let iconClass = item.isLevel ? 'level-icon' : (item.isKey ? 'key-icon' : 'attr-icon');

                const nameEditable = !isReadOnly ? 'contenteditable="true"' : '';
                html += \`<td class="item-name-cell">\${indentSpace}<span class="icon \${iconClass}" onclick="toggleKey('\${id}')">\${iconHtml}</span><span class="editable" \${nameEditable} onblur="updateLocalData('\${id}', 'name', this.innerText)">\${item.name}</span></td>\`;
                
                // Type
                const typeDisabled = isReadOnly || item.isLevel ? "readonly" : "";
                html += \`<td style="position: relative"><input type="text" list="gx-types" class="type-input" value="\${item.type || ''}" onchange="updateLocalData('\${id}', 'type', this.value)" \${typeDisabled} autocomplete="off"/></td>\`;
                
                // Desc
                const descEditable = !isReadOnly && !item.isLevel;
                html += \`<td class="editable" contenteditable="\${descEditable}" onblur="updateLocalData('\${id}', 'description', this.innerText)">\${item.description || ''}</td>\`;
                
                // Formula
                const formulaEditable = !isReadOnly && !item.isLevel;
                html += \`<td class="editable formula-text" contenteditable="\${formulaEditable}" onblur="updateLocalData('\${id}', 'formula', this.innerText)">\${item.formula || ''}</td>\`;
                
                // Nullable
                if (item.isLevel) {
                  html += '<td></td>';
                } else {
                  const val = item.nullable || 'No';
                  html += \`<td>
                    <select class="nullable-select" \${isReadOnly ? 'disabled' : ''} onchange="updateLocalData('\${id}', 'nullable', this.value)">
                      <option value="No" \${val === 'No' ? 'selected' : ''}>No</option>
                      <option value="Yes" \${val === 'Yes' ? 'selected' : ''}>Yes</option>
                      <option value="Managed" \${val === 'Managed' || val === 'Compatible' ? 'selected' : ''}>Mng</option>
                    </select>
                  </td>\`;
                }
                
                html += \`<td class="actions-cell">\${!isReadOnly ? \`<button class="btn-del" onclick="deleteRow('\${id}')">×</button>\` : ''}</td>\`;
                html += '</tr>';
                
                if (item.children && item.children.length > 0) {
                  renderLevel(item.children, levelNum + 1, id);
                }
              });
            }

            renderLevel(currentData.children, 0, "");
            html += '</tbody></table>';
            document.getElementById('content').innerHTML = html;
          }

          function getItemById(id) {
            const parts = id.split('-').map(Number);
            let current = currentData.children;
            let target = null;
            for (let i = 0; i < parts.length; i++) {
              target = current[parts[i]];
              if (i < parts.length - 1) current = target.children;
            }
            return target;
          }

          function updateLocalData(id, field, value) {
            if (isReadOnly) return;
            const item = getItemById(id);
            if (item) item[field] = value;
          }

          function toggleKey(id) {
            if (isReadOnly) return;
            const item = getItemById(id);
            if (item && !item.isLevel) {
              item.isKey = !item.isKey;
              renderStructure();
            }
          }

          function addRow(isLevel) {
            if (isReadOnly) return;
            const newItem = {
              name: isLevel ? 'NewLevel' : 'NewAttribute',
              isLevel: isLevel, isKey: false,
              type: isLevel ? '' : 'Character(20)',
              description: '', formula: '', nullable: 'No', children: []
            };
            currentData.children.push(newItem);
            renderStructure();
          }

          function deleteRow(id) {
            if (isReadOnly) return;
            const parts = id.split('-').map(Number);
            let current = currentData.children;
            for (let i = 0; i < parts.length - 1; i++) {
              current = current[parts[i]].children;
            }
            current.splice(parts[parts.length - 1], 1);
            renderStructure();
          }

          function requestSave() {
            if (isReadOnly) return;
            setStatus('Saving...', '');
            vscode.postMessage({ command: 'save', structure: currentData });
          }
        </script>
      </body>
      </html>
    `;

    try {
      const result = await provider.callGateway({
        method: "execute_command",
        params: {
          module: "Structure",
          action: "GetVisualStructure",
          target: target,
        },
      });
      if (result && !result.error) {
        panel.webview.postMessage({ type: "update", structure: result });
      } else {
        panel.webview.html = `<h1>Error: ${result?.error || "Unknown error"}</h1>`;
      }

      // Handle messages from the webview
      panel.webview.onDidReceiveMessage(async (message) => {
        if (message.command === "save") {
          try {
            const saveResult = await provider.callGateway(
              {
                method: "execute_command",
                params: {
                  module: "Structure",
                  action: "UpdateVisualStructure",
                  target: target,
                  payload: JSON.stringify(message.structure),
                },
              },
              180000,
            ); // 180s timeout for heavy KBs
            if (saveResult && saveResult.status === "Success") {
              panel.webview.postMessage({ type: "success" });
              vscode.window.setStatusBarMessage(
                `$(check) Structure saved for ${objName}`,
                3000,
              );
            } else {
              panel.webview.postMessage({
                type: "error",
                message: saveResult?.error || "Save failed",
              });
            }
          } catch (e) {
            panel.webview.postMessage({ type: "error", message: String(e) });
          }
        }
      });
    } catch (e) {
      panel.webview.html = `<h1>Critical Error: ${e}</h1>`;
    }
  };

  // Switch Part Commands
  const switchPart = async (partName: string, uri?: vscode.Uri) => {
    let targetUri = uri;
    if (!targetUri) {
      const editor = vscode.window.activeTextEditor;
      if (editor && editor.document.uri.scheme === "genexus") {
        targetUri = editor.document.uri;
      } else {
        // Try to find any visible genexus editor
        const gxEditor = vscode.window.visibleTextEditors.find(
          (e) => e.document.uri.scheme === "genexus",
        );
        if (gxEditor) targetUri = gxEditor.document.uri;
      }
    }

    if (!targetUri) return;
    const isTransaction = targetUri.path.includes("/Transaction/");
    const isTable = targetUri.path.includes("/Table/");

    // If it's Structure for Transaction/Table, or if it's Layout or Indexes, open Webview
    if (
      (partName === "Structure" && (isTransaction || isTable)) ||
      partName === "Layout" ||
      partName === "Indexes"
    ) {
      if (partName === "Structure") {
        await showVisualStructure(targetUri);
      } else if (partName === "Indexes") {
        await showVisualIndexes(targetUri);
      } else {
        await showWebviewLayout(targetUri);
      }
      return;
    }

    console.log(
      `[Nexus IDE] switchPart: Changing part to ${partName} for ${targetUri.fsPath}`,
    );
    provider.setPart(targetUri, partName);

    // Force VS Code to re-open the same URI to pick up the new part content
    await vscode.commands.executeCommand("vscode.open", targetUri, {
      preview: false,
      preserveFocus: true,
    });

    vscode.window.setStatusBarMessage(`Switched to ${partName}`, 2000);
    updateActiveContext(targetUri);
  };

  context.subscriptions.push(
    vscode.commands.registerCommand("nexus-ide.switchPart.Source", (u) =>
      switchPart("Source", u),
    ),
    vscode.commands.registerCommand("nexus-ide.switchPart.Source.active", (u) =>
      switchPart("Source", u),
    ),
    vscode.commands.registerCommand("nexus-ide.switchPart.Rules", (u) =>
      switchPart("Rules", u),
    ),
    vscode.commands.registerCommand("nexus-ide.switchPart.Rules.active", (u) =>
      switchPart("Rules", u),
    ),
    vscode.commands.registerCommand("nexus-ide.switchPart.Events", (u) =>
      switchPart("Events", u),
    ),
    vscode.commands.registerCommand("nexus-ide.switchPart.Events.active", (u) =>
      switchPart("Events", u),
    ),
    vscode.commands.registerCommand("nexus-ide.switchPart.Variables", (u) =>
      switchPart("Variables", u),
    ),
    vscode.commands.registerCommand(
      "nexus-ide.switchPart.Variables.active",
      (u) => switchPart("Variables", u),
    ),
    vscode.commands.registerCommand("nexus-ide.switchPart.Structure", (u) =>
      switchPart("Structure", u),
    ),
    vscode.commands.registerCommand(
      "nexus-ide.switchPart.Structure.active",
      (u) => switchPart("Structure", u),
    ),
    vscode.commands.registerCommand("nexus-ide.switchPart.Layout", (u) =>
      switchPart("Layout", u),
    ),
    vscode.commands.registerCommand("nexus-ide.switchPart.Layout.active", (u) =>
      switchPart("Layout", u),
    ),
    vscode.commands.registerCommand("nexus-ide.switchPart.Indexes", (u) =>
      switchPart("Indexes", u),
    ),
    vscode.commands.registerCommand(
      "nexus-ide.switchPart.Indexes.active",
      (u) => switchPart("Indexes", u),
    ),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand("nexus-ide.showVisualStructure", (u) =>
      showVisualStructure(u),
    ),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand("nexus-ide.forceSave", async () => {
      const editor = vscode.window.activeTextEditor;
      if (!editor || editor.document.uri.scheme !== "genexus") return;

      const uri = editor.document.uri;
      const content = Buffer.from(editor.document.getText(), "utf8");

      await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: `GeneXus: Salvando ${uri.fsPath}...`,
          cancellable: false,
        },
        async () => {
          try {
            await provider.triggerSave(uri, content);
            vscode.window.setStatusBarMessage(
              `$(check) Salvo: ${uri.fsPath}`,
              5000,
            );
          } catch (e) {
            vscode.window.showErrorMessage(`Erro ao salvar: ${e}`);
          }
        },
      );
    }),
  );

  // Add Workspace command
  context.subscriptions.push(
    vscode.commands.registerCommand("nexus-ide.openKb", async () => {
      vscode.workspace.updateWorkspaceFolders(
        vscode.workspace.workspaceFolders
          ? vscode.workspace.workspaceFolders.length
          : 0,
        null,
        {
          uri: vscode.Uri.parse("genexus:/"),
          name: "GeneXus KB",
        },
      );
      await provider.initKb();
      await vscode.commands.executeCommand("nexus-ide.indexKb");
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand("nexus-ide.indexKb", async () => {
      await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: "GeneXus: Real-time Indexing KB...",
          cancellable: false,
        },
        async (progress) => {
          try {
            isBulkIndexing = true;
            // Trigger background indexing
            await provider.callGateway({
              method: "execute_command",
              params: { module: "KB", action: "BulkIndex" },
            });

            // Polling loop
            let isDone = false;
            let lastProcessed = 0;

            while (!isDone) {
              await new Promise((resolve) => setTimeout(resolve, 1000));
              const status = await provider.callGateway({
                method: "execute_command",
                params: { module: "KB", action: "GetIndexStatus" },
              });

              if (status && status.isIndexing) {
                const current = status.processed || 0;
                const total = status.total || 1;
                const increment = ((current - lastProcessed) / total) * 100;
                lastProcessed = current;

                progress.report({
                  message: `${status.status} (${current}/${total})`,
                  increment: increment > 0 ? increment : undefined,
                });
              } else if (
                status &&
                (status.status === "Complete" || !status.isIndexing)
              ) {
                isDone = true;
              }
            }
          } catch (e) {
            vscode.window.showErrorMessage(`Indexing failed: ${e}`);
          } finally {
            isBulkIndexing = false;
          }
        },
      );
      treeProvider.refresh();
      vscode.window.showInformationMessage(
        "GeneXus KB Indexed! Hierarchy and Search are now ready.",
      );
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand("nexus-ide.refreshTree", () => {
      provider.clearDirCache();
      treeProvider.refresh();
      vscode.window.setStatusBarMessage(
        "$(refresh) Nexus IDE: Tree refreshed",
        3000,
      );
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand(
      "nexus-ide.refreshDiagnostics",
      async () => {
        await diagnosticProvider.refreshAll();
      },
    ),
  );

  // Intercept standard Save event for debug and Linter On-Save
  context.subscriptions.push(
    vscode.workspace.onDidSaveTextDocument((doc: vscode.TextDocument) => {
      if (doc.uri.scheme === "genexus") {
        console.log(
          `[Nexus IDE] onDidSaveTextDocument (Linter Triggered): ${doc.uri.path}`,
        );
        // PERFORMANCE: Pequeno delay para garantir que o Worker terminou o commit da transação
        setTimeout(() => {
          diagnosticProvider.refreshAll();
        }, 1000);
      }
    }),
  );

  // Monitor willSave
  context.subscriptions.push(
    vscode.workspace.onWillSaveTextDocument(
      (e: vscode.TextDocumentWillSaveEvent) => {
        if (e.document.uri.scheme === "genexus") {
          console.log(
            `[Nexus IDE] onWillSaveTextDocument: ${e.document.uri.path}`,
          );
        }
      },
    ),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand("nexus-ide.runReorg", async () => {
      await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: "Checking and Installing Database (Reorg)...",
          cancellable: false,
        },
        async () => {
          const result = await provider.callGateway({
            method: "execute_command",
            params: { module: "Build", action: "Reorg" },
          });
          if (result && result.status === "Success") {
            vscode.window.showInformationMessage("Reorganization successful.");
          } else {
            vscode.window.showErrorMessage(
              "Reorganization failed: " +
                (result?.output || result?.error || "Unknown error"),
            );
          }
        },
      );
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand(
      "nexus-ide.buildObject",
      async (item?: GxTreeItem) => {
        let objName = "";
        if (item && item.gxName) {
          objName = item.gxName;
        } else {
          const editor = vscode.window.activeTextEditor;
          if (editor && editor.document.uri.scheme === "genexus") {
            const path = decodeURIComponent(
              editor.document.uri.path.substring(1),
            );
            objName = path.split("/").pop()!.replace(".gx", "");
          }
        }

        if (!objName) {
          vscode.window.showErrorMessage("Selecione um objeto para Build.");
          return;
        }

        const outputChannel =
          vscode.window.createOutputChannel("GeneXus Build");
        outputChannel.show();
        outputChannel.appendLine(
          `[Build] Iniciando 'Build with this only' para: ${objName}...`,
        );

        await vscode.window.withProgress(
          {
            location: vscode.ProgressLocation.Notification,
            title: `GeneXus: Building ${objName}...`,
            cancellable: false,
          },
          async (progress) => {
            try {
              const result = await provider.callGateway(
                {
                  method: "execute_command",
                  params: { module: "Build", action: "Build", target: objName },
                },
                600000,
              ); // 10 minutes timeout for Build

              if (result && result.status === "Success") {
                outputChannel.appendLine(
                  result.output || "Build finalizado com sucesso.",
                );
                vscode.window.showInformationMessage(
                  `Build de ${objName} concluído!`,
                );
              } else {
                const errorMsg = result
                  ? result.error || result.output || JSON.stringify(result)
                  : "Resposta vazia do Gateway";
                outputChannel.appendLine(`ERRO NO BUILD:\n${errorMsg}`);
                vscode.window.showErrorMessage(
                  `Falha no Build de ${objName}. Verifique o log de saída.`,
                );
              }
            } catch (e) {
              outputChannel.appendLine(`ERRO CRÍTICO: ${e}`);
              vscode.window.showErrorMessage(
                `Erro ao chamar o Gateway para Build: ${e}`,
              );
            }
          },
        );
      },
    ),
  );

  // Optimized Search Provider for CTRL+P (Instant Response)
  if ((vscode.workspace as any).registerFileSearchProvider) {
    (vscode.workspace as any).registerFileSearchProvider("genexus", {
      provideFileSearchResults: async (
        query: any,
        _options: any,
        token: vscode.CancellationToken,
      ): Promise<vscode.Uri[]> => {
        console.log(
          `[Nexus IDE] Global search triggered with pattern: "${query.pattern}"`,
        );
        try {
          const pattern = query.pattern || "";
          if (pattern.length < 2) return [];

          // Directly call the gateway with @quick optimization for CTRL+P
          const result = await provider.callGateway({
            method: "execute_command",
            params: {
              module: "Search",
              target: pattern + " @quick",
              limit: 100,
            },
          });

          if (token.isCancellationRequested) return [];

          if (result && result.results) {
            console.log(
              `[Nexus IDE] Global search found ${result.results.length} results`,
            );
            return result.results.map((obj: any) =>
              vscode.Uri.parse(`genexus:/${obj.type}/${obj.name}.gx`),
            );
          }
        } catch (e) {
          console.error("[Nexus IDE] Global search error:", e);
        }
        return [];
      },
    });
  }

  context.subscriptions.push(
    vscode.commands.registerCommand(
      "nexus-ide.createVariable",
      async (uri: vscode.Uri, varName: string) => {
        const path = decodeURIComponent(uri.path.substring(1));
        const objName = path.split("/").pop()!.replace(".gx", "");

        await vscode.window.withProgress(
          {
            location: vscode.ProgressLocation.Notification,
            title: `Creating Variable &${varName}...`,
            cancellable: false,
          },
          async () => {
            try {
              const result = await provider.callGateway({
                method: "execute_command",
                params: {
                  module: "Write",
                  action: "AddVariable",
                  target: objName,
                  varName: varName,
                },
              });
              if (result && result.status === "Success") {
                vscode.window.showInformationMessage(
                  `Variable &${varName} created successfully.`,
                );
              } else {
                vscode.window.showErrorMessage(
                  `Failed to create variable: ${result.error || JSON.stringify(result)}`,
                );
              }
            } catch (e) {
              vscode.window.showErrorMessage(`Error: ${e}`);
            }
          },
        );
      },
    ),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand("nexus-ide.copyMcpConfig", async () => {
      const port = vscode.workspace
        .getConfiguration()
        .get("genexus.mcpPort", 5000);
      const snippet = JSON.stringify(
        {
          mcpServers: {
            genexus: {
              command: "npx",
              args: [
                "-y",
                "@modelcontextprotocol/server-http",
                `http://localhost:${port}/api/command`,
              ],
            },
          },
        },
        null,
        2,
      );

      await vscode.env.clipboard.writeText(snippet);
      vscode.window.showInformationMessage(
        "MCP Configuration snippet for Claude/Copilot copied to clipboard!",
      );
    }),
  );

  // --- ELITE EDITION COMMANDS ---

  context.subscriptions.push(
    vscode.commands.registerCommand("nexus-ide.newObject", async () => {
      const types = Object.keys(TYPE_SUFFIX);
      const selectedType = await vscode.window.showQuickPick(types, {
        placeHolder: "Select object type to create",
      });

      if (!selectedType) return;

      const name = await vscode.window.showInputBox({
        prompt: `Enter name for the new ${selectedType}`,
        placeHolder: "e.g. MyNewObject",
      });

      if (!name) return;

      await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: `Creating ${selectedType}: ${name}...`,
          cancellable: false,
        },
        async () => {
          try {
            const result = await provider.callGateway({
              method: "execute_command",
              params: {
                module: "KB",
                action: "CreateObject",
                type: selectedType,
                name: name,
              },
            });

            if (result && result.status === "Success") {
              vscode.window.showInformationMessage(
                `${selectedType} '${name}' created!`,
              );

              // Open the new object
              const suffix = TYPE_SUFFIX[selectedType]
                ? `.${TYPE_SUFFIX[selectedType]}`
                : "";
              const uri = vscode.Uri.parse(`genexus:/${name}${suffix}.gx`);
              await vscode.commands.executeCommand("vscode.open", uri);

              // Refresh explorer
              provider.clearDirCache();
              treeProvider.refresh();
            } else {
              vscode.window.showErrorMessage(
                `Failed to create object: ${result?.error || "Unknown error"}`,
              );
            }
          } catch (e) {
            vscode.window.showErrorMessage(`Error creating object: ${e}`);
          }
        },
      );
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand("nexus-ide.rebuildAll", async () => {
      await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: "Rebuilding All objects...",
          cancellable: false,
        },
        async () => {
          try {
            await provider.callGateway({
              method: "execute_command",
              params: { module: "Build", action: "RebuildAll" },
            });
            vscode.window.showInformationMessage("Rebuild All completed!");
          } catch (e) {
            vscode.window.showErrorMessage(`Rebuild All failed: ${e}`);
          }
        },
      );
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand("nexus-ide.renameAttribute", async () => {
      const oldName = await vscode.window.showInputBox({
        prompt: "Enter current attribute name",
        placeHolder: "e.g. CustomerName",
      });

      if (!oldName) return;

      const newName = await vscode.window.showInputBox({
        prompt: `Rename attribute '${oldName}' to:`,
        placeHolder: "e.g. CustomerFullName",
      });

      if (!newName) return;

      await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: `Renaming Attribute ${oldName} -> ${newName}...`,
          cancellable: false,
        },
        async () => {
          try {
            const result = await provider.callGateway({
              method: "execute_command",
              params: {
                module: "Refactor",
                action: "RenameAttribute",
                target: oldName,
                payload: newName,
              },
            });

            if (result && result.status === "Success") {
              vscode.window.showInformationMessage(
                `Attribute renamed successfully!`,
              );
              provider.clearDirCache();
              treeProvider.refresh();
            } else {
              vscode.window.showErrorMessage(
                `Failed to rename: ${result?.error || "Unknown error"}`,
              );
            }
          } catch (e) {
            vscode.window.showErrorMessage(`Error renaming attribute: ${e}`);
          }
        },
      );
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand(
      "nexus-ide.viewHistory",
      async (item?: GxTreeItem) => {
        // ... (lógica de abertura do painel continua igual até o listener)
        let objName = "";
        if (item && item.gxName) {
          objName = item.gxName;
        } else {
          const editor = vscode.window.activeTextEditor;
          if (editor && editor.document.uri.scheme === "genexus") {
            const path = decodeURIComponent(
              editor.document.uri.path.substring(1),
            );
            objName = path.split("/").pop()!.replace(".gx", "");
          }
        }

        if (!objName) {
          vscode.window.showErrorMessage(
            "Abra ou selecione um objeto para ver o histórico.",
          );
          return;
        }

        const panel = vscode.window.createWebviewPanel(
          "gxHistory",
          `Histórico: ${objName}`,
          vscode.ViewColumn.Beside,
          { enableScripts: true },
        );

        panel.webview.html = `<h1>Carregando Histórico de ${objName}...</h1>`;

        panel.onDidDispose(() => {
          historyProvider.clear(objName);
        });

        try {
          const result = await provider.callGateway({
            method: "execute_command",
            params: { module: "History", action: "List", target: objName },
          });

          if (result && result.history) {
            // Montar tabela HTML bonita com o histórico
            let rows = "";
            if (Array.isArray(result.history)) {
              // Ordenar: Mais recente primeiro
              const sortedHistory = [...result.history].reverse();

              rows = sortedHistory
                .map(
                  (rev: any) => `
                        <tr>
                            <td style="padding: 10px; border-bottom: 1px solid #333; font-weight: bold; color: #007acc;">#${rev.version || rev.Id || ""}</td>
                            <td style="padding: 10px; border-bottom: 1px solid #333; white-space: nowrap;">${rev.date || rev.Date || ""}</td>
                            <td style="padding: 10px; border-bottom: 1px solid #333;">${rev.user || rev.User || ""}</td>
                            <td style="padding: 10px; border-bottom: 1px solid #333; font-style: italic; color: #aaa;">${rev.comment || rev.Comment || '<span style="opacity: 0.5;">Sem comentário</span>'}</td>
                            <td style="padding: 10px; border-bottom: 1px solid #333; text-align: center;">
                                <button onclick="viewDiff(${rev.version || rev.Id})" style="background: #007acc; color: white; border: none; padding: 4px 8px; cursor: pointer; border-radius: 2px;">Comparar (Diff)</button>
                            </td>
                        </tr>
                    `,
                )
                .join("");
            } else if (typeof result.history === "string") {
              rows = `<tr><td colspan="5"><pre>${result.history}</pre></td></tr>`;
            }

            panel.webview.html = `
                    <!DOCTYPE html>
                    <html>
                    <head>
                        <style>
                            body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif; padding: 20px; background-color: #1e1e1e; color: #ccc; }
                            table { width: 100%; border-collapse: collapse; margin-top: 20px; background: #252526; box-shadow: 0 4px 6px rgba(0,0,0,0.3); border-radius: 4px; overflow: hidden; }
                            th { background-color: #333333; color: #fff; text-align: left; padding: 12px 10px; font-size: 0.9em; text-transform: uppercase; letter-spacing: 1px; }
                            tr:hover { background-color: #2d2d2d; }
                            h2 { color: #fff; border-bottom: 1px solid #444; padding-bottom: 10px; }
                            .badge { background: #007acc; color: white; padding: 2px 6px; border-radius: 3px; font-size: 0.8em; margin-left: 10px; }
                        </style>
                    </head>
                    <body>
                        <h2>Histórico de Revisões: ${objName} <span class="badge">SDK Nativo</span></h2>
                        <table>
                            <thead>
                                <tr>
                                    <th>Rev</th>
                                    <th>Data / Hora</th>
                                    <th>Autor</th>
                                    <th>Comentário de Commit</th>
                                    <th>Ações</th>
                                </tr>
                            </thead>
                            <tbody>${rows || '<tr><td colspan="5" style="padding: 20px; text-align: center;">Nenhuma revisão encontrada.</td></tr>'}</tbody>
                        </table>

                        <script>
                            const vscode = acquireVsCodeApi();
                            function viewDiff(vId) {
                                vscode.postMessage({ command: 'viewDiff', versionId: vId, objName: '${objName}' });
                            }
                        </script>
                    </body>
                    </html>
                `;

            // Listener para mensagens vindas da Webview
            panel.webview.onDidReceiveMessage(async (message) => {
              if (message.command === "viewDiff") {
                vscode.window.setStatusBarMessage(
                  `$(sync~spin) Buscando Revisão #${message.versionId} para Diff...`,
                  3000,
                );
                try {
                  // Re-resolve currentUri inside the listener context to ensure it's captured correctly
                  let listenerCurrentUri: vscode.Uri | undefined;
                  const editor = vscode.window.activeTextEditor;
                  if (editor && editor.document.uri.scheme === "genexus") {
                    listenerCurrentUri = editor.document.uri;
                  } else {
                    // Fallback based on objName if editor changed
                    listenerCurrentUri = vscode.Uri.parse(
                      `genexus:/Procedure/${message.objName}.gx`,
                    );
                  }

                  const codeResult = await provider.callGateway({
                    method: "execute_command",
                    params: {
                      module: "History",
                      action: "get_source",
                      target: message.objName,
                      versionId: message.versionId,
                    },
                  });

                  if (codeResult && codeResult.source) {
                    const historicalContent = Buffer.from(
                      codeResult.source,
                      "base64",
                    ).toString("utf8");

                    // Criar URI virtual para a versão histórica
                    const historyUri = vscode.Uri.parse(
                      `gx-history:/v${message.versionId}/${message.objName}.gx`,
                    );
                    historyProvider.update(historyUri, historicalContent);

                    // Executar o comando nativo de Diff do VS Code
                    await vscode.commands.executeCommand(
                      "vscode.diff",
                      historyUri,
                      listenerCurrentUri!,
                      `${message.objName} (Revisão #${message.versionId}) ↔ (Atual)`,
                    );
                  } else {
                    vscode.window.showErrorMessage(
                      `Não foi possível recuperar o código da versão ${message.versionId}`,
                    );
                  }
                } catch (e) {
                  vscode.window.showErrorMessage(`Erro ao buscar versão: ${e}`);
                }
              }
            });
          } else {
            panel.webview.html =
              "<h1>Histórico não encontrado ou módulo indisponível.</h1>" +
              (result?.error ? `<p>${result.error}</p>` : "");
          }
        } catch (e) {
          panel.webview.html = `<h1>Erro Crítico: ${e}</h1>`;
        }
      },
    ),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand(
      "nexus-ide.generateDiagram",
      async (item?: GxTreeItem) => {
        let objName = "";
        if (item && item.gxName) {
          objName = item.gxName;
        } else {
          const editor = vscode.window.activeTextEditor;
          if (editor && editor.document.uri.scheme === "genexus") {
            const path = decodeURIComponent(
              editor.document.uri.path.substring(1),
            );
            objName = path.split("/").pop()!.replace(".gx", "");
          }
        }

        if (!objName) {
          vscode.window.showErrorMessage(
            "Selecione um objeto para gerar o diagrama.",
          );
          return;
        }

        const panel = vscode.window.createWebviewPanel(
          "gxDiagram",
          `${objName} Diagram`,
          vscode.ViewColumn.Beside,
          { enableScripts: true },
        );

        panel.webview.html = `<h1>Gerando Diagrama para ${objName}...</h1>`;

        try {
          const result = await provider.callGateway({
            method: "execute_command",
            params: {
              module: "Visualizer",
              action: "GenerateGraph",
              target: objName,
            },
          });

          if (result && result.mermaid) {
            panel.webview.html = `
                    <!DOCTYPE html>
                    <html>
                    <head>
                        <script src="https://cdn.jsdelivr.net/npm/mermaid/dist/mermaid.min.js"></script>
                        <script>mermaid.initialize({ startOnLoad: true });</script>
                    </head>
                    <body>
                        <pre class="mermaid">
                            ${result.mermaid}
                        </pre>
                    </body>
                    </html>
                `;
          } else {
            panel.webview.html =
              "<h1>Não foi possível gerar o diagrama para este objeto.</h1>";
          }
        } catch (e) {
          panel.webview.html = `<h1>Erro: ${e}</h1>`;
        }
      },
    ),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand("nexus-ide.autoFix", async () => {
      const editor = vscode.window.activeTextEditor;
      if (!editor || editor.document.uri.scheme !== "genexus") {
        vscode.window.showErrorMessage(
          "Abra um objeto GeneXus para usar o Auto-Fix.",
        );
        return;
      }

      const diagnostics = vscode.languages.getDiagnostics(editor.document.uri);
      const error = diagnostics.find(
        (d) => d.severity === vscode.DiagnosticSeverity.Error,
      );

      if (!error) {
        vscode.window.showInformationMessage(
          "Nenhum erro de build encontrado neste objeto.",
        );
        return;
      }

      await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: "AI Analyzing error and proposing fix...",
          cancellable: false,
        },
        async () => {
          try {
            const result = await provider.callGateway({
              method: "execute_command",
              params: {
                module: "Analyze",
                action: "ExplainCode",
                target: editor.document.uri.path
                  .split("/")
                  .pop()
                  ?.replace(".gx", ""),
                payload: JSON.stringify({
                  error: error.message,
                  line: error.range.start.line,
                  code: editor.document.getText(),
                }),
              },
            });

            if (result && result.fix) {
              const choice = await vscode.window.showInformationMessage(
                `AI Fix suggested: ${result.summary}\nApply fix?`,
                "Apply Fix",
                "Cancel",
              );

              if (choice === "Apply Fix") {
                const edit = new vscode.WorkspaceEdit();
                const fullRange = new vscode.Range(
                  editor.document.positionAt(0),
                  editor.document.positionAt(editor.document.getText().length),
                );
                edit.replace(editor.document.uri, fullRange, result.fix);
                await vscode.workspace.applyEdit(edit);
                vscode.window.showInformationMessage(
                  "AI Fix applied! Save to verify.",
                );
              }
            } else {
              vscode.window.showWarningMessage(
                "AI não conseguiu encontrar uma solução automática para este erro.",
              );
            }
          } catch (e) {
            vscode.window.showErrorMessage(`Erro no Auto-Fix: ${e}`);
          }
        },
      );
    }),
  );
}

export function deactivate() {
  if (backendProcess && !backendProcess.killed) {
    console.log("[Nexus IDE] Stopping MCP Gateway...");
    backendProcess.kill();
  }
}
