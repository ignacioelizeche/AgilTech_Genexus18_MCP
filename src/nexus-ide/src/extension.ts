import * as vscode from 'vscode';
import * as path from 'path';
import { GxFileSystemProvider } from './gxFileSystem';
import { GxDocumentSymbolProvider } from './symbolProvider';
import { GxTreeProvider, GxTreeItem } from './gxTreeProvider';
import { GxDefinitionProvider } from './definitionProvider';
import { GxHoverProvider } from './hoverProvider';
import { GxCompletionItemProvider } from './completionProvider';
import { GxInlineCompletionItemProvider } from './inlineCompletionProvider';
import { GxDiagnosticProvider } from './diagnosticProvider';
import { GxReferenceProvider } from './referenceProvider';
import { GxWorkspaceSymbolProvider } from './workspaceSymbolProvider';
import { GxCodeLensProvider } from './codeLensProvider';
import { GxSignatureHelpProvider } from './signatureHelpProvider';
import { GxCodeActionProvider } from './codeActionProvider';
import { GxRenameProvider } from './renameProvider';
import { GxFormatProvider } from './formatProvider';

export async function activate(context: vscode.ExtensionContext) {
    const provider = new GxFileSystemProvider();

    // Custom Tree Provider for the GeneXus Explorer view (icons + ordering)
    const treeProvider = new GxTreeProvider(
        (cmd) => provider.callGateway(cmd),
        context.extensionUri,
    );
    const treeView = vscode.window.createTreeView('genexusExplorer', {
        treeDataProvider: treeProvider,
        showCollapseAll: true,
    });
    context.subscriptions.push(treeView);
    
    context.subscriptions.push(vscode.workspace.registerFileSystemProvider('genexus', provider, {
        isCaseSensitive: true,
        isReadonly: false
    }));

    context.subscriptions.push(vscode.languages.registerDocumentSymbolProvider('genexus', new GxDocumentSymbolProvider()));
    context.subscriptions.push(vscode.languages.registerDefinitionProvider('genexus', new GxDefinitionProvider((cmd) => provider.callGateway(cmd))));
    context.subscriptions.push(vscode.languages.registerHoverProvider('genexus', new GxHoverProvider((cmd) => provider.callGateway(cmd))));
    context.subscriptions.push(vscode.languages.registerCompletionItemProvider('genexus', new GxCompletionItemProvider((cmd) => provider.callGateway(cmd)), '.', '&'));
    context.subscriptions.push(vscode.languages.registerInlineCompletionItemProvider('genexus', new GxInlineCompletionItemProvider()));
    context.subscriptions.push(vscode.languages.registerSignatureHelpProvider('genexus', new GxSignatureHelpProvider((cmd) => provider.callGateway(cmd)), '(', ','));
    context.subscriptions.push(vscode.languages.registerCodeActionsProvider('genexus', new GxCodeActionProvider((cmd) => provider.callGateway(cmd)), {
        providedCodeActionKinds: [GxCodeActionProvider.kind]
    }));
    context.subscriptions.push(vscode.languages.registerRenameProvider('genexus', new GxRenameProvider((cmd) => provider.callGateway(cmd))));
    context.subscriptions.push(vscode.languages.registerDocumentFormattingEditProvider('genexus', new GxFormatProvider((cmd) => provider.callGateway(cmd))));

    const diagnosticProvider = new GxDiagnosticProvider((cmd) => provider.callGateway(cmd));
    diagnosticProvider.subscribeToEvents(context);

    context.subscriptions.push(vscode.languages.registerWorkspaceSymbolProvider(new GxWorkspaceSymbolProvider((cmd) => provider.callGateway(cmd))));
    context.subscriptions.push(vscode.languages.registerCodeLensProvider('genexus', new GxCodeLensProvider((cmd) => provider.callGateway(cmd))));
    context.subscriptions.push(vscode.languages.registerReferenceProvider('genexus', new GxReferenceProvider((cmd) => provider.callGateway(cmd))));

    context.subscriptions.push(vscode.commands.registerCommand('gx.showReferences', async (objName: string) => {
        const activeEditor = vscode.window.activeTextEditor;
        if (!activeEditor) return;

        // Trigger native reference peek/view at current position (top of file where CodeLens is)
        await vscode.commands.executeCommand('editor.action.showReferences', 
            activeEditor.document.uri, 
            new vscode.Position(0, 0), 
            [] // We let the provider find them
        );
    }));

    // --- INSTANT ACTIVATION ---
    // 1. Add virtual folder IMMEDIATELY (No delay, no await)
    const hasGxFolder = vscode.workspace.workspaceFolders?.some(f => f.uri.scheme === 'genexus');
    if (!hasGxFolder) {
        vscode.workspace.updateWorkspaceFolders(
            vscode.workspace.workspaceFolders ? vscode.workspace.workspaceFolders.length : 0, 
            null, 
            { uri: vscode.Uri.parse('genexus:/'), name: "GeneXus KB" }
        );
    }

    // 2. Fire and forget KB initialization
    provider.initKb().then(() => {
        console.log("[Nexus IDE] KB background init complete.");
        treeProvider.refresh();
    }).catch(e => {
        console.error("[Nexus IDE] Background init failed:", e);
    });

    const webviewPanels = new Map<string, vscode.WebviewPanel>();

    // Webview layout preview
    const showWebviewLayout = async (targetUri: vscode.Uri) => {
        const path = decodeURIComponent(targetUri.path.substring(1));
        const objName = path.split('/').pop()!.replace('.gx', '');
        const uriKey = targetUri.toString();

        if (webviewPanels.has(uriKey)) {
            webviewPanels.get(uriKey)!.reveal(vscode.ViewColumn.Beside);
            return;
        }

        const panel = vscode.window.createWebviewPanel(
            'gxLayout',
            `${objName} Layout`,
            vscode.ViewColumn.Beside,
            { enableScripts: true, enableCommandUris: true }
        );

        webviewPanels.set(uriKey, panel);
        panel.onDidDispose(() => webviewPanels.delete(uriKey));

        panel.webview.html = "<h1>Carregando Layout...</h1>";
        
        try {
            const result = await provider.callGateway({
                method: "execute_command",
                params: { module: 'Read', action: 'ExtractSource', target: objName, part: 'Layout' }
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

    // Switch Part Commands
    const switchPart = async (partName: string, uri?: vscode.Uri) => {
        let targetUri = uri;
        if (!targetUri) {
            const editor = vscode.window.activeTextEditor;
            if (editor && editor.document.uri.scheme === 'genexus') {
                targetUri = editor.document.uri;
            } else {
                // Try to find any visible genexus editor
                const gxEditor = vscode.window.visibleTextEditors.find(e => e.document.uri.scheme === 'genexus');
                if (gxEditor) targetUri = gxEditor.document.uri;
            }
        }
        
        if (!targetUri) return;

        // If it's Layout, open a Webview Beside the editor
        if (partName === 'Layout') {
            await showWebviewLayout(targetUri);
            return;
        }

        console.log(`[Nexus IDE] switchPart: Changing part to ${partName} for ${targetUri.fsPath}`);
        provider.setPart(targetUri, partName);
        
        // Force VS Code to re-open the same URI to pick up the new part content
        await vscode.commands.executeCommand('vscode.open', targetUri, {
            preview: false,
            preserveFocus: true
        });

        vscode.window.setStatusBarMessage(`Switched to ${partName}`, 2000);
    };

    context.subscriptions.push(vscode.commands.registerCommand('nexus-ide.switchPart.Source', (u) => switchPart('Source', u)));
    context.subscriptions.push(vscode.commands.registerCommand('nexus-ide.switchPart.Rules', (u) => switchPart('Rules', u)));
    context.subscriptions.push(vscode.commands.registerCommand('nexus-ide.switchPart.Events', (u) => switchPart('Events', u)));
    context.subscriptions.push(vscode.commands.registerCommand('nexus-ide.switchPart.Variables', (u) => switchPart('Variables', u)));
    context.subscriptions.push(vscode.commands.registerCommand('nexus-ide.switchPart.Structure', (u) => switchPart('Structure', u)));
    context.subscriptions.push(vscode.commands.registerCommand('nexus-ide.switchPart.Layout', (u) => switchPart('Layout', u)));

    context.subscriptions.push(vscode.commands.registerCommand('nexus-ide.forceSave', async () => {
        const editor = vscode.window.activeTextEditor;
        if (!editor || editor.document.uri.scheme !== 'genexus') return;

        const uri = editor.document.uri;
        const content = Buffer.from(editor.document.getText(), 'utf8');
        
        await vscode.window.withProgress({
            location: vscode.ProgressLocation.Notification,
            title: `GeneXus: Salvando ${uri.fsPath}...`,
            cancellable: false
        }, async () => {
            try {
                await provider.triggerSave(uri, content);
                vscode.window.setStatusBarMessage(`$(check) Salvo: ${uri.fsPath}`, 5000);
            } catch (e) {
                vscode.window.showErrorMessage(`Erro ao salvar: ${e}`);
            }
        });
    }));

    // Add Workspace command
    context.subscriptions.push(vscode.commands.registerCommand('nexus-ide.openKb', async () => {
        vscode.workspace.updateWorkspaceFolders(vscode.workspace.workspaceFolders ? vscode.workspace.workspaceFolders.length : 0, null, {
            uri: vscode.Uri.parse('genexus:/'),
            name: "GeneXus KB"
        });
        await provider.initKb();
        await vscode.commands.executeCommand('nexus-ide.indexKb');
    }));

    context.subscriptions.push(vscode.commands.registerCommand('nexus-ide.indexKb', async () => {
        await vscode.window.withProgress({
            location: vscode.ProgressLocation.Notification,
            title: "GeneXus: Real-time Indexing KB...",
            cancellable: false
        }, async (progress) => {
            try {
                // Trigger background indexing
                await provider.callGateway({
                    method: "execute_command",
                    params: { module: 'KB', action: 'BulkIndex' }
                });

                // Polling loop
                let isDone = false;
                let lastProcessed = 0;

                while (!isDone) {
                    await new Promise(resolve => setTimeout(resolve, 1000));
                    const status = await provider.callGateway({
                        method: "execute_command",
                        params: { module: 'KB', action: 'GetIndexStatus' }
                    });

                    if (status && status.isIndexing) {
                        const current = status.processed || 0;
                        const total = status.total || 1;
                        const increment = ((current - lastProcessed) / total) * 100;
                        lastProcessed = current;
                        
                        progress.report({ 
                            message: `${status.status} (${current}/${total})`,
                            increment: increment > 0 ? increment : undefined
                        });
                    } else if (status && (status.status === "Complete" || !status.isIndexing)) {
                        isDone = true;
                    }
                }
            } catch (e) {
                vscode.window.showErrorMessage(`Indexing failed: ${e}`);
            }
        });
        treeProvider.refresh();
        vscode.window.showInformationMessage("GeneXus KB Indexed! Hierarchy and Search are now ready.");
    }));
    
    context.subscriptions.push(vscode.commands.registerCommand('nexus-ide.refreshTree', () => {
        provider.clearDirCache();
        treeProvider.refresh();
        vscode.window.setStatusBarMessage('$(refresh) Nexus IDE: Tree refreshed', 3000);
    }));

    context.subscriptions.push(vscode.commands.registerCommand('nexus-ide.refreshDiagnostics', async () => {
        await diagnosticProvider.refreshAll();
    }));

    // Intercept standard Save event for debug
    context.subscriptions.push(vscode.workspace.onDidSaveTextDocument((doc: vscode.TextDocument) => {
        if (doc.uri.scheme === 'genexus') {
            console.log(`[Nexus IDE] onDidSaveTextDocument: ${doc.uri.path}`);
        }
    }));

    // Monitor willSave
    context.subscriptions.push(vscode.workspace.onWillSaveTextDocument((e: vscode.TextDocumentWillSaveEvent) => {
        if (e.document.uri.scheme === 'genexus') {
            console.log(`[Nexus IDE] onWillSaveTextDocument: ${e.document.uri.path}`);
        }
    }));

    context.subscriptions.push(vscode.commands.registerCommand('nexus-ide.runReorg', async () => {
        await vscode.window.withProgress({
            location: vscode.ProgressLocation.Notification,
            title: "Checking and Installing Database (Reorg)...",
            cancellable: false
        }, async () => {
            const result = await provider.callGateway({
                method: 'execute_command',
                params: { module: 'Build', action: 'Reorg' }
            });
            if (result && result.status === 'Success') {
                vscode.window.showInformationMessage("Reorganization successful.");
            } else {
                vscode.window.showErrorMessage("Reorganization failed: " + (result?.output || result?.error || 'Unknown error'));
            }
        });
    }));

    context.subscriptions.push(vscode.commands.registerCommand('nexus-ide.buildObject', async (item?: GxTreeItem) => {
        let objName = '';
        if (item && item.gxName) {
            objName = item.gxName;
        } else {
            const editor = vscode.window.activeTextEditor;
            if (editor && editor.document.uri.scheme === 'genexus') {
                const path = decodeURIComponent(editor.document.uri.path.substring(1));
                objName = path.split('/').pop()!.replace('.gx', '');
            }
        }

        if (!objName) {
            vscode.window.showErrorMessage("Selecione um objeto para Build.");
            return;
        }

        const outputChannel = vscode.window.createOutputChannel("GeneXus Build");
        outputChannel.show();
        outputChannel.appendLine(`[Build] Iniciando 'Build with this only' para: ${objName}...`);

        await vscode.window.withProgress({
            location: vscode.ProgressLocation.Notification,
            title: `GeneXus: Building ${objName}...`,
            cancellable: false
        }, async (progress) => {
            try {
                const result = await provider.callGateway({
                    method: "execute_command",
                    params: { module: 'Build', action: 'Build', target: objName }
                }, 600000); // 10 minutes timeout for Build

                if (result && result.status === 'Success') {
                    outputChannel.appendLine(result.output || "Build finalizado com sucesso.");
                    vscode.window.showInformationMessage(`Build de ${objName} concluído!`);
                } else {
                    const errorMsg = result ? (result.error || result.output || JSON.stringify(result)) : "Resposta vazia do Gateway";
                    outputChannel.appendLine(`ERRO NO BUILD:\n${errorMsg}`);
                    vscode.window.showErrorMessage(`Falha no Build de ${objName}. Verifique o log de saída.`);
                }
            } catch (e) {
                outputChannel.appendLine(`ERRO CRÍTICO: ${e}`);
                vscode.window.showErrorMessage(`Erro ao chamar o Gateway para Build: ${e}`);
            }
        });
    }));

    // Optimized Search Provider for CTRL+P (Instant Response)
    if ((vscode.workspace as any).registerFileSearchProvider) {
        (vscode.workspace as any).registerFileSearchProvider('genexus', {
            provideFileSearchResults: async (query: any, _options: any, token: vscode.CancellationToken): Promise<vscode.Uri[]> => {
                try {
                    const pattern = query.pattern || "";
                    if (pattern.length < 2) return [];

                    // Directly call the gateway without artificial delay
                    const result = await provider.callGateway({
                        method: "execute_command",
                        params: { module: 'Search', target: pattern, limit: 1000 }
                    });

                    if (token.isCancellationRequested) return [];

                    if (result && result.results) {
                        return result.results.map((obj: any) => vscode.Uri.parse(`genexus:/${obj.type}/${obj.name}.gx`));
                    }
                } catch (e) {
                    console.error("[Nexus IDE] Global search error:", e);
                }
                return [];
            }
        });
    }

    context.subscriptions.push(vscode.commands.registerCommand('nexus-ide.createVariable', async (uri: vscode.Uri, varName: string) => {
        const path = decodeURIComponent(uri.path.substring(1));
        const objName = path.split('/').pop()!.replace('.gx', '');

        await vscode.window.withProgress({
            location: vscode.ProgressLocation.Notification,
            title: `Creating Variable &${varName}...`,
            cancellable: false
        }, async () => {
            try {
                const result = await provider.callGateway({
                    method: 'execute_command',
                    params: { module: 'Write', action: 'AddVariable', target: objName, varName: varName }
                });
                if (result && result.status === 'Success') {
                    vscode.window.showInformationMessage(`Variable &${varName} created successfully.`);
                } else {
                    vscode.window.showErrorMessage(`Failed to create variable: ${result.error || JSON.stringify(result)}`);
                }
            } catch (e) {
                vscode.window.showErrorMessage(`Error: ${e}`);
            }
        });
    }));
}

export function deactivate() {}