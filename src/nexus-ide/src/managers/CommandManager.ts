import * as vscode from "vscode";
import { GxFileSystemProvider, TYPE_SUFFIX } from "../gxFileSystem";
import { GxTreeProvider, GxTreeItem } from "../gxTreeProvider";
import { GxDiagnosticProvider } from "../diagnosticProvider";
import { ContextManager } from "./ContextManager";
import { StructureView } from "../webviews/StructureView";
import { IndexView } from "../webviews/IndexView";
import { LayoutView } from "../webviews/LayoutView";
import { HistoryView } from "../webviews/HistoryView";
import { DiagramView } from "../webviews/DiagramView";

export class CommandManager {
  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly provider: GxFileSystemProvider,
    private readonly treeProvider: GxTreeProvider,
    private readonly diagnosticProvider: GxDiagnosticProvider,
    private readonly contextManager: ContextManager,
    private readonly historyProvider: any
  ) {}

  register() {
    this.registerSwitchPartCommands();
    this.registerBuildCommands();
    this.registerKbCommands();
    this.registerRefactorCommands();
    this.registerMiscCommands();
  }

  private registerSwitchPartCommands() {
    const switchPart = async (partName: string, uri?: vscode.Uri) => {
      let targetUri = uri;
      if (!targetUri) {
        const editor = vscode.window.activeTextEditor;
        if (editor && editor.document.uri.scheme === "genexus") {
          targetUri = editor.document.uri;
        } else {
          const gxEditor = vscode.window.visibleTextEditors.find(
            (e) => e.document.uri.scheme === "genexus"
          );
          if (gxEditor) targetUri = gxEditor.document.uri;
        }
      }

      if (!targetUri) return;
      const isTransaction = targetUri.path.includes("/Transaction/");
      const isTable = targetUri.path.includes("/Table/");

      if (
        (partName === "Structure" && (isTransaction || isTable)) ||
        partName === "Layout" ||
        partName === "Indexes"
      ) {
        if (partName === "Structure") {
          await StructureView.show(targetUri, this.provider);
        } else if (partName === "Indexes") {
          await IndexView.show(targetUri, this.provider);
        } else {
          await LayoutView.show(targetUri, this.provider);
        }
        return;
      }

      this.provider.setPart(targetUri, partName);
      await vscode.commands.executeCommand("vscode.open", targetUri, {
        preview: false,
        preserveFocus: true,
      });

      this.contextManager.setStatusBarMessage(`Switched to ${partName}`, 2000);
      this.contextManager.updateActiveContext(targetUri);
    };

    const parts = ["Source", "Rules", "Events", "Variables", "Structure", "Layout", "Indexes"];
    for (const part of parts) {
      this.context.subscriptions.push(
        vscode.commands.registerCommand(`nexus-ide.switchPart.${part}`, (u) => switchPart(part, u)),
        vscode.commands.registerCommand(`nexus-ide.switchPart.${part}.active`, (u) => switchPart(part, u))
      );
    }

    this.context.subscriptions.push(
        vscode.commands.registerCommand("nexus-ide.showVisualStructure", (u) => StructureView.show(u, this.provider))
    );
  }

  private registerBuildCommands() {
    this.context.subscriptions.push(
      vscode.commands.registerCommand("nexus-ide.runReorg", async () => {
        await vscode.window.withProgress(
          { location: vscode.ProgressLocation.Notification, title: "Checking and Installing Database (Reorg)...", cancellable: false },
          async () => {
            const result = await this.provider.callGateway({
              method: "execute_command",
              params: { module: "Build", action: "Reorg" },
            });
            if (result && result.status === "Success") {
              vscode.window.showInformationMessage("Reorganization successful.");
            } else {
              vscode.window.showErrorMessage("Reorganization failed: " + (result?.output || result?.error || "Unknown error"));
            }
          }
        );
      }),

      vscode.commands.registerCommand("nexus-ide.buildObject", async (item?: GxTreeItem) => {
        let objName = "";
        if (item && item.gxName) {
          objName = item.gxName;
        } else {
          const editor = vscode.window.activeTextEditor;
          if (editor && editor.document.uri.scheme === "genexus") {
            const pathStr = decodeURIComponent(editor.document.uri.path.substring(1));
            objName = pathStr.split("/").pop()!.replace(".gx", "");
          }
        }

        if (!objName) {
          vscode.window.showErrorMessage("Selecione um objeto para Build.");
          return;
        }

        const outputChannel = vscode.window.createOutputChannel("GeneXus Build");
        outputChannel.show();
        outputChannel.appendLine(`[Build] Iniciando 'Build with this only' para: ${objName}...`);

        await vscode.window.withProgress(
          { location: vscode.ProgressLocation.Notification, title: `GeneXus: Building ${objName}...`, cancellable: false },
          async (progress) => {
            try {
              const result = await this.provider.callGateway(
                { method: "execute_command", params: { module: "Build", action: "Build", target: objName } },
                600000
              );

              if (result && result.status === "Success") {
                outputChannel.appendLine(result.output || "Build finalizado com sucesso.");
                vscode.window.showInformationMessage(`Build de ${objName} concluído!`);
              } else {
                const errorMsg = result ? result.error || result.output || JSON.stringify(result) : "Resposta vazia do Gateway";
                outputChannel.appendLine(`ERRO NO BUILD:\n${errorMsg}`);
                vscode.window.showErrorMessage(`Falha no Build de ${objName}. Verifique o log de saída.`);
              }
            } catch (e) {
              outputChannel.appendLine(`ERRO CRÍTICO: ${e}`);
              vscode.window.showErrorMessage(`Erro ao chamar o Gateway para Build: ${e}`);
            }
          }
        );
      }),

      vscode.commands.registerCommand("nexus-ide.rebuildAll", async () => {
        await vscode.window.withProgress(
          { location: vscode.ProgressLocation.Notification, title: "Rebuilding All objects...", cancellable: false },
          async () => {
            try {
              await this.provider.callGateway({
                method: "execute_command",
                params: { module: "Build", action: "RebuildAll" },
              });
              vscode.window.showInformationMessage("Rebuild All completed!");
            } catch (e) {
              vscode.window.showErrorMessage(`Rebuild All failed: ${e}`);
            }
          }
        );
      })
    );
  }

  private registerKbCommands() {
    this.context.subscriptions.push(
      vscode.commands.registerCommand("nexus-ide.openKb", async () => {
        vscode.workspace.updateWorkspaceFolders(
          vscode.workspace.workspaceFolders ? vscode.workspace.workspaceFolders.length : 0,
          null,
          { uri: vscode.Uri.parse("genexus:/"), name: "GeneXus KB" }
        );
        await this.provider.initKb();
        await vscode.commands.executeCommand("nexus-ide.indexKb");
      }),

      vscode.commands.registerCommand("nexus-ide.indexKb", async () => {
        await vscode.window.withProgress(
          { location: vscode.ProgressLocation.Notification, title: "GeneXus: Real-time Indexing KB...", cancellable: false },
          async (progress) => {
            try {
              (this.context as any).isBulkIndexing = true;
              await this.provider.callGateway({
                method: "execute_command",
                params: { module: "KB", action: "BulkIndex" },
              });

              let isDone = false;
              let lastProcessed = 0;

              while (!isDone) {
                await new Promise((resolve) => setTimeout(resolve, 1000));
                const status = await this.provider.callGateway({
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
                } else if (status && (status.status === "Complete" || !status.isIndexing)) {
                  isDone = true;
                }
              }
            } catch (e) {
              vscode.window.showErrorMessage(`Indexing failed: ${e}`);
            } finally {
                (this.context as any).isBulkIndexing = false;
            }
          }
        );
        this.treeProvider.refresh();
        vscode.window.showInformationMessage("GeneXus KB Indexed! Hierarchy and Search are now ready.");
      }),

      vscode.commands.registerCommand("nexus-ide.newObject", async () => {
        const types = Object.keys(TYPE_SUFFIX);
        const selectedType = await vscode.window.showQuickPick(types, { placeHolder: "Select object type to create" });
        if (!selectedType) return;
        const name = await vscode.window.showInputBox({ prompt: `Enter name for the new ${selectedType}`, placeHolder: "e.g. MyNewObject" });
        if (!name) return;

        await vscode.window.withProgress(
          { location: vscode.ProgressLocation.Notification, title: `Creating ${selectedType}: ${name}...`, cancellable: false },
          async () => {
            try {
              const result = await this.provider.callGateway({
                method: "execute_command",
                params: { module: "KB", action: "CreateObject", type: selectedType, name: name },
              });
              if (result && result.status === "Success") {
                vscode.window.showInformationMessage(`${selectedType} '${name}' created!`);
                const suffix = TYPE_SUFFIX[selectedType] ? `.${TYPE_SUFFIX[selectedType]}` : "";
                const uri = vscode.Uri.parse(`genexus:/${name}${suffix}.gx`);
                await vscode.commands.executeCommand("vscode.open", uri);
                this.provider.clearDirCache();
                this.treeProvider.refresh();
              } else {
                vscode.window.showErrorMessage(`Failed to create object: ${result?.error || "Unknown error"}`);
              }
            } catch (e) {
              vscode.window.showErrorMessage(`Error creating object: ${e}`);
            }
          }
        );
      })
    );
  }

  private registerRefactorCommands() {
    this.context.subscriptions.push(
      vscode.commands.registerCommand("nexus-ide.renameAttribute", async () => {
        const oldName = await vscode.window.showInputBox({ prompt: "Enter current attribute name", placeHolder: "e.g. CustomerName" });
        if (!oldName) return;
        const newName = await vscode.window.showInputBox({ prompt: `Rename attribute '${oldName}' to:`, placeHolder: "e.g. CustomerFullName" });
        if (!newName) return;

        await vscode.window.withProgress(
          { location: vscode.ProgressLocation.Notification, title: `Renaming Attribute ${oldName} -> ${newName}...`, cancellable: false },
          async () => {
            try {
              const result = await this.provider.callGateway({
                method: "execute_command",
                params: { module: "Refactor", action: "RenameAttribute", target: oldName, payload: newName },
              });
              if (result && result.status === "Success") {
                vscode.window.showInformationMessage(`Attribute renamed successfully!`);
                this.provider.clearDirCache();
                this.treeProvider.refresh();
              } else {
                vscode.window.showErrorMessage(`Failed to rename: ${result?.error || "Unknown error"}`);
              }
            } catch (e) {
              vscode.window.showErrorMessage(`Error renaming attribute: ${e}`);
            }
          }
        );
      }),

      vscode.commands.registerCommand("nexus-ide.createVariable", async (uri: vscode.Uri, varName: string) => {
        const pathStr = decodeURIComponent(uri.path.substring(1));
        const objName = pathStr.split("/").pop()!.replace(".gx", "");
        await vscode.window.withProgress(
          { location: vscode.ProgressLocation.Notification, title: `Creating Variable &${varName}...`, cancellable: false },
          async () => {
            try {
              const result = await this.provider.callGateway({
                method: "execute_command",
                params: { module: "Write", action: "AddVariable", target: objName, varName: varName },
              });
              if (result && result.status === "Success") {
                vscode.window.showInformationMessage(`Variable &${varName} created successfully.`);
              } else {
                vscode.window.showErrorMessage(`Failed to create variable: ${result.error || JSON.stringify(result)}`);
              }
            } catch (e) {
              vscode.window.showErrorMessage(`Error: ${e}`);
            }
          }
        );
      })
    );
  }

  private registerMiscCommands() {
    this.context.subscriptions.push(
      vscode.commands.registerCommand("nexus-ide.refreshTree", () => {
        this.provider.clearDirCache();
        this.treeProvider.refresh();
        this.contextManager.setStatusBarMessage("$(refresh) Nexus IDE: Tree refreshed", 3000);
      }),

      vscode.commands.registerCommand("nexus-ide.refreshDiagnostics", async () => {
        await this.diagnosticProvider.refreshAll();
      }),

      vscode.commands.registerCommand("nexus-ide.forceSave", async () => {
        const editor = vscode.window.activeTextEditor;
        if (!editor || editor.document.uri.scheme !== "genexus") return;
        const uri = editor.document.uri;
        const content = Buffer.from(editor.document.getText(), "utf8");

        await vscode.window.withProgress(
          { location: vscode.ProgressLocation.Notification, title: `GeneXus: Salvando ${uri.fsPath}...`, cancellable: false },
          async () => {
            try {
              await this.provider.triggerSave(uri, content);
              this.contextManager.setStatusBarMessage(`$(check) Salvo: ${uri.fsPath}`, 5000);
            } catch (e) {
              vscode.window.showErrorMessage(`Erro ao salvar: ${e}`);
            }
          }
        );
      }),

      vscode.commands.registerCommand("gx.showReferences", async (objName: string) => {
        const activeEditor = vscode.window.activeTextEditor;
        if (!activeEditor) return;
        await vscode.commands.executeCommand("editor.action.showReferences", activeEditor.document.uri, new vscode.Position(0, 0), []);
      }),

      vscode.commands.registerCommand("nexus-ide.viewHistory", (u) => HistoryView.show(u, this.provider, this.historyProvider)),
      vscode.commands.registerCommand("nexus-ide.generateDiagram", (u) => DiagramView.show(u, this.provider)),
      
      vscode.commands.registerCommand("nexus-ide.copyMcpConfig", async () => {
        const port = vscode.workspace.getConfiguration().get("genexus.mcpPort", 5000);
        const snippet = JSON.stringify({ mcpServers: { genexus: { command: "npx", args: ["-y", "@modelcontextprotocol/server-http", `http://localhost:${port}/api/command`] } } }, null, 2);
        await vscode.env.clipboard.writeText(snippet);
        vscode.window.showInformationMessage("MCP Configuration snippet for Claude/Copilot copied to clipboard!");
      }),

      vscode.commands.registerCommand("nexus-ide.autoFix", async () => {
        const editor = vscode.window.activeTextEditor;
        if (!editor || editor.document.uri.scheme !== "genexus") {
          vscode.window.showErrorMessage("Abra um objeto GeneXus para usar o Auto-Fix.");
          return;
        }

        const diagnostics = vscode.languages.getDiagnostics(editor.document.uri);
        const error = diagnostics.find((d) => d.severity === vscode.DiagnosticSeverity.Error);

        if (!error) {
          vscode.window.showInformationMessage("Nenhum erro de build encontrado neste objeto.");
          return;
        }

        await vscode.window.withProgress(
          { location: vscode.ProgressLocation.Notification, title: "AI Analyzing error and proposing fix...", cancellable: false },
          async () => {
            try {
              const result = await this.provider.callGateway({
                method: "execute_command",
                params: {
                  module: "Analyze",
                  action: "ExplainCode",
                  target: editor.document.uri.path.split("/").pop()?.replace(".gx", ""),
                  payload: JSON.stringify({ error: error.message, line: error.range.start.line, code: editor.document.getText() }),
                },
              });

              if (result && result.fix) {
                const choice = await vscode.window.showInformationMessage(`AI Fix suggested: ${result.summary}\nApply fix?`, "Apply Fix", "Cancel");
                if (choice === "Apply Fix") {
                  const edit = new vscode.WorkspaceEdit();
                  const fullRange = new vscode.Range(editor.document.positionAt(0), editor.document.positionAt(editor.document.getText().length));
                  edit.replace(editor.document.uri, fullRange, result.fix);
                  await vscode.workspace.applyEdit(edit);
                  vscode.window.showInformationMessage("AI Fix applied! Save to verify.");
                }
              } else {
                vscode.window.showWarningMessage("AI não conseguiu encontrar uma solução automática para este erro.");
              }
            } catch (e) {
              vscode.window.showErrorMessage(`Erro no Auto-Fix: ${e}`);
            }
          }
        );
      })
    );
  }
}
