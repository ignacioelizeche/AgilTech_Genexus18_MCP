import * as vscode from "vscode";
import * as fs from "fs";
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

let backendManager: BackendManager;

export async function activate(context: vscode.ExtensionContext) {
  const config = vscode.workspace.getConfiguration();
  const provider = new GxFileSystemProvider();

  // Set dynamic port from config
  const port = config.get("genexus.mcpPort", 5000);
  provider.baseUrl = `http://localhost:${port}/api/command`;

  // 1. Initialize Managers
  backendManager = new BackendManager(context);
  const contextManager = new ContextManager(context, provider);
  const shadowService = new GxShadowService(provider.baseUrl);
  provider.setShadowService(shadowService);

  const diagnosticProvider = new GxDiagnosticProvider(
    (cmd) => provider.callGateway(cmd),
    provider
  );
  provider.setDiagnosticProvider(diagnosticProvider);

  const treeProvider = new GxTreeProvider(
    (cmd) => provider.callGateway(cmd),
    context.extensionUri
  );

  // 2. Register Shadow Sync
  const shadowManager = new ShadowManager(context, shadowService, diagnosticProvider);
  shadowManager.register();

  // 3. Register UI Components
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

  const actionsProvider = new GxActionsProvider();
  vscode.window.createTreeView("genexusActions", {
    treeDataProvider: actionsProvider,
    showCollapseAll: false,
  });

  // 4. Register Filesystem & Providers
  context.subscriptions.push(
    vscode.workspace.registerFileSystemProvider("genexus", provider, {
      isCaseSensitive: true,
      isReadonly: false,
    })
  );

  const providerManager = new ProviderManager(context, provider);
  providerManager.register();

  // 5. Register Commands
  // Get historyProvider from providerManager to pass it to commandManager
  // Since historyProvider is internal to ProviderManager, we need a way to access it or just create it at extension level.
  // For simplicity, let's look at how ProviderManager handles it. It's a TextDocumentContentProvider.
  // I'll modify ProviderManager to expose it.
  
  const commandManager = new CommandManager(
    context,
    provider,
    treeProvider,
    diagnosticProvider,
    contextManager,
    providerManager.historyProvider
  );
  commandManager.register();

  contextManager.register();
  diagnosticProvider.subscribeToEvents(context);

  // 6. Start Backend & Init KB
  backendManager.start(provider).then(() => {
    (context as any).isBulkIndexing = false;
  });

  // 7. Instant Activation (Virtual Folder)
  const hasGxFolder = vscode.workspace.workspaceFolders?.some((f) => f.uri.scheme === "genexus");
  if (!hasGxFolder) {
    vscode.workspace.updateWorkspaceFolders(
      vscode.workspace.workspaceFolders ? vscode.workspace.workspaceFolders.length : 0,
      null,
      { uri: vscode.Uri.parse("genexus:/"), name: "GeneXus KB" }
    );
  }

  // 8. Background KB Initialization & Transparent Auto-Indexing
  provider.initKb().then(async () => {
    console.log("[Nexus IDE] KB background init complete.");
    treeProvider.refresh();

    // Check if shadowRoot exists and has content
    const shadowPath = shadowService.shadowRoot;
    const shadowDirExists = fs.existsSync(shadowPath) && fs.readdirSync(shadowPath).length > 0;

    if (!shadowDirExists) {
      vscode.window.setStatusBarMessage("$(sync~spin) GeneXus: Preparando ambiente (Index & Shadow)...", 10000);
      try {
        (context as any).isBulkIndexing = true;
        await provider.callGateway({
          method: "execute_command",
          params: { module: "KB", action: "BulkIndex" },
        });

        // Polling loop
        let isDone = false;
        while (!isDone) {
          await new Promise((resolve) => setTimeout(resolve, 2000));
          const status = await provider.callGateway({
            method: "execute_command",
            params: { module: "KB", action: "GetIndexStatus" },
          });

          if (status && status.status === "Complete") {
            isDone = true;
            vscode.window.setStatusBarMessage("$(check) GeneXus: Ambiente Pronto!", 5000);
          } else if (status && status.isIndexing) {
            vscode.window.setStatusBarMessage(`$(sync~spin) GeneXus: Indexando (${status.processed}/${status.total})...`, 2000);
          } else {
            isDone = true;
          }
        }
      } catch (e) {
        console.error("[Nexus IDE] Auto-index failed:", e);
      } finally {
        (context as any).isBulkIndexing = false;
      }
    }
  });

  // 9. Watch for standard Save event for Linter
  context.subscriptions.push(
    vscode.workspace.onDidSaveTextDocument((doc: vscode.TextDocument) => {
      if (doc.uri.scheme === "genexus") {
        setTimeout(() => {
          diagnosticProvider.refreshAll();
        }, 1000);
      }
    })
  );
}

export function deactivate() {
  if (backendManager) {
    backendManager.stop();
  }
}
