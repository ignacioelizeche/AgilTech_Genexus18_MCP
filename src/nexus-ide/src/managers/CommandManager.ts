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
import { PropertiesView } from "../webviews/PropertiesView";
import { 
  GX_SCHEME, 
  CONFIG_SECTION, 
  CONFIG_MCP_PORT, 
  DEFAULT_MCP_PORT,
  MODULE_BUILD,
  MODULE_KB,
  MODULE_ANALYZE,
  MODULE_REFACTOR,
  MODULE_WRITE,
  MODULE_HEALTH,
  DEFAULT_STATUS_BAR_TIMEOUT,
  STATE_KEY_MCP_DISCOVERY,
} from "../constants";

import { GxUriParser } from "../utils/GxUriParser";

type DiscoverySnapshot = {
  tools: any[];
  resources: any[];
  resourceTemplates?: any[];
  prompts: any[];
  fetchedAt: string;
};

type DiscoveryResourceItem = vscode.QuickPickItem & {
  itemType: "resource" | "template";
  value: string;
};

export class CommandManager {
  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly provider: GxFileSystemProvider,
    private readonly treeProvider: GxTreeProvider,
    private readonly diagnosticProvider: GxDiagnosticProvider,
    private readonly contextManager: ContextManager,
    private readonly historyProvider: any,
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
      const targetUri = uri || GxUriParser.getActiveGxUri();

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

    const parts = [
      "Source",
      "Rules",
      "Events",
      "Variables",
      "Structure",
      "Layout",
      "Indexes",
      "Documentation",
      "Help",
    ];
    for (const part of parts) {
      this.context.subscriptions.push(
        vscode.commands.registerCommand(`nexus-ide.switchPart.${part}`, (u) =>
          switchPart(part, u),
        ),
        vscode.commands.registerCommand(
          `nexus-ide.switchPart.${part}.active`,
          (u) => switchPart(part, u),
        ),
      );
    }

    this.context.subscriptions.push(
      vscode.commands.registerCommand("nexus-ide.showVisualStructure", (u) =>
        StructureView.show(u, this.provider),
      ),
      vscode.commands.registerCommand(
        "nexus-ide.repairVirtualFolder",
        async () => {
          const { addKbFolder } = require("../extension");
          await addKbFolder(this.context);
          vscode.window.showInformationMessage(
            "Attempted to repair Virtual Folder mount. Check Explorer.",
          );
        },
      ),
    );
  }

  private registerBuildCommands() {
    this.context.subscriptions.push(
      vscode.commands.registerCommand("nexus-ide.runReorg", async () => {
        await vscode.window.withProgress(
          {
            location: vscode.ProgressLocation.Notification,
            title: "Checking and Installing Database (Reorg)...",
            cancellable: false,
          },
          async () => {
            const result = await this.provider.callMcpTool(
              "genexus_lifecycle",
              { action: "reorg" },
              600000,
            );
            if (result && result.status === "Success") {
              vscode.window.showInformationMessage(
                "Reorganization successful.",
              );
            } else {
              vscode.window.showErrorMessage(
                "Reorganization failed: " +
                  (result?.output || result?.error || "Unknown error"),
              );
            }
          },
        );
      }),

      vscode.commands.registerCommand(
        "nexus-ide.buildObject",
        async (item?: GxTreeItem) => {
          let objName = item?.gxName;

          if (!objName) {
            const targetUri = GxUriParser.getActiveGxUri();
            if (targetUri) {
              objName = GxUriParser.getObjectName(targetUri);
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
                const result = await this.provider.callMcpTool(
                  "genexus_lifecycle",
                  {
                    action: "build",
                    target: objName,
                  },
                  600000,
                );

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

      vscode.commands.registerCommand("nexus-ide.rebuildAll", async () => {
        await vscode.window.withProgress(
          {
            location: vscode.ProgressLocation.Notification,
            title: "Rebuilding All objects...",
            cancellable: false,
          },
          async () => {
            try {
              await this.provider.callMcpTool(
                "genexus_lifecycle",
                { action: "rebuild" },
                600000,
              );
              vscode.window.showInformationMessage("Rebuild All completed!");
            } catch (e) {
              vscode.window.showErrorMessage(`Rebuild All failed: ${e}`);
            }
          },
        );
      }),

      vscode.commands.registerCommand(
        "nexus-ide.getSQL",
        async (item?: GxTreeItem) => {
          let objName = item?.gxName;

          if (!objName) {
            const targetUri = GxUriParser.getActiveGxUri();
            if (targetUri) {
              objName = GxUriParser.getObjectName(targetUri);
            }
          }

          if (!objName) {
            vscode.window.showErrorMessage("Selecione uma Transação ou Tabela.");
            return;
          }

          const outputChannel = vscode.window.createOutputChannel("GeneXus SQL");
          outputChannel.show();
          outputChannel.appendLine(`[SQL] Extraindo DDL para: ${objName}...`);

          await vscode.window.withProgress(
            {
              location: vscode.ProgressLocation.Notification,
              title: `GeneXus: Generating SQL for ${objName}...`,
              cancellable: false,
            },
            async () => {
              try {
                const result = await this.provider.callMcpTool(
                  "genexus_get_sql",
                  { name: objName },
                  30000,
                );

                if (result && result.ddl) {
                  outputChannel.clear();
                  outputChannel.appendLine(`-- SQL DDL para ${objName} (${result.dbms || "Oracle"})`);
                  outputChannel.appendLine(`-- Fonte: ${result.source}`);
                  outputChannel.appendLine("");
                  outputChannel.appendLine(result.ddl);
                  vscode.window.showInformationMessage(`SQL de ${objName} extraído com sucesso!`);
                } else {
                  const errorMsg = result?.error || "Não foi possível extrair o SQL.";
                  outputChannel.appendLine(`ERRO: ${errorMsg}`);
                  vscode.window.showErrorMessage(`Falha ao obter SQL de ${objName}.`);
                }
              } catch (e) {
                outputChannel.appendLine(`ERRO CRÍTICO: ${e}`);
                vscode.window.showErrorMessage(`Erro ao chamar MCP: ${e}`);
              }
            },
          );
        },
      ),
    );
  }

  private registerKbCommands() {
    this.context.subscriptions.push(
      vscode.commands.registerCommand("nexus-ide.indexKb", async () => {
        await vscode.window.withProgress(
          {
            location: vscode.ProgressLocation.Notification,
            title: "GeneXus: Real-time Indexing KB...",
            cancellable: false,
          },
          async (progress) => {
            try {
              this.provider.isBulkIndexing = true;
              await this.provider.callMcpTool(
                "genexus_lifecycle",
                { action: "index" },
                300000,
              );

              let isDone = false;
              let lastProcessed = 0;

              while (!isDone) {
                await new Promise((resolve) => setTimeout(resolve, 1000));
                const status = await this.provider.callMcpTool(
                  "genexus_lifecycle",
                  { action: "status" },
                  15000,
                );

                if (status && status.isIndexing) {
                  const current = status.processed || 0;
                  const total = status.total || 1;
                  const increment = ((current - lastProcessed) / total) * 100;
                  lastProcessed = current;

                  progress.report({
                    message: `${status.status} (${current}/${total})`,
                    increment: increment > 0 ? increment : undefined,
                  });

                  // Update status bar as well for visibility outside notification
                  vscode.window.setStatusBarMessage(
                    `$(sync~spin) GeneXus: Indexando (${current}/${total})...`,
                    2000,
                  );
                } else if (
                  status &&
                  (status.status === "Complete" ||
                    (!status.isIndexing && status.status !== "Indexing"))
                ) {
                  isDone = true;
                }
              }
            } catch (e) {
              vscode.window.showErrorMessage(`Indexing failed: ${e}`);
            } finally {
              this.provider.isBulkIndexing = false;
            }
          },
        );
        this.treeProvider.refresh();
        vscode.window.showInformationMessage(
          "GeneXus KB Indexed! Hierarchy and Search are now ready.",
        );
      }),

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
              const result = await this.provider.callMcpTool(
                "genexus_create_object",
                {
                  type: selectedType,
                  name: name,
                },
                60000,
              );
              if (result && result.status === "Success") {
                vscode.window.showInformationMessage(
                  `${selectedType} '${name}' created!`,
                );
                const suffix = TYPE_SUFFIX[selectedType]
                  ? `.${TYPE_SUFFIX[selectedType]}`
                  : "";
                const uri = vscode.Uri.from({
                  scheme: GX_SCHEME,
                  path: `/${selectedType}/${name}${suffix}.gx`,
                });
                await vscode.commands.executeCommand("vscode.open", uri);
                this.provider.clearDirCache();
                this.treeProvider.refresh();
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
  }

  private registerRefactorCommands() {
    this.context.subscriptions.push(
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
              const result = await this.provider.callMcpTool(
                "genexus_refactor",
                {
                  action: "RenameAttribute",
                  target: oldName,
                  newName: newName,
                },
                300000,
              );
              if (result && result.status === "Success") {
                vscode.window.showInformationMessage(
                  `Attribute renamed successfully!`,
                );
                this.provider.clearDirCache();
                this.treeProvider.refresh();
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

      vscode.commands.registerCommand(
        "nexus-ide.createVariable",
        async (uri: vscode.Uri, varName: string) => {
          const pathStr = decodeURIComponent(uri.path.substring(1));
          const objName = pathStr.split("/").pop()!.replace(".gx", "");
          await vscode.window.withProgress(
            {
              location: vscode.ProgressLocation.Notification,
              title: `Creating Variable &${varName}...`,
              cancellable: false,
            },
            async () => {
              try {
                const result = await this.provider.callMcpTool(
                  "genexus_add_variable",
                  {
                    name: objName,
                    varName: varName,
                  },
                  60000,
                );
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
  }

  private registerMiscCommands() {
    this.context.subscriptions.push(
      vscode.commands.registerCommand("nexus-ide.showMcpDiscovery", async () => {
        const snapshot = this.context.globalState.get<DiscoverySnapshot>(
          STATE_KEY_MCP_DISCOVERY,
        );
        if (!snapshot) {
          vscode.window.showWarningMessage(
            "Nenhum snapshot MCP encontrado. Aguarde o discovery ou reinicie a extensao.",
          );
          return;
        }

        const doc = await vscode.workspace.openTextDocument({
          language: "json",
          content: JSON.stringify(snapshot, null, 2),
        });
        await vscode.window.showTextDocument(doc, { preview: false });
      }),

      vscode.commands.registerCommand("nexus-ide.openMcpResource", async () => {
        const snapshot = this.context.globalState.get<DiscoverySnapshot>(
          STATE_KEY_MCP_DISCOVERY,
        );
        if (!snapshot) {
          vscode.window.showWarningMessage(
            "Nenhum snapshot MCP encontrado. Aguarde o discovery ou reinicie a extensao.",
          );
          return;
        }

        const resourceItems: DiscoveryResourceItem[] = (snapshot.resources ?? []).map((resource) => ({
          label: resource.name || resource.uri,
          description: resource.uri,
          detail: resource.description,
          itemType: "resource" as const,
          value: resource.uri,
        }));
        const templateItems: DiscoveryResourceItem[] = (snapshot.resourceTemplates ?? []).map(
          (template) => ({
            label: template.name || template.uriTemplate,
            description: template.uriTemplate,
            detail: template.description,
            itemType: "template" as const,
            value: template.uriTemplate,
          }),
        );

        const selected = await vscode.window.showQuickPick<DiscoveryResourceItem>(
          [...resourceItems, ...templateItems],
          { placeHolder: "Selecione um MCP resource ou resource template" },
        );
        if (!selected) return;

        let uri = selected.value;
        if (selected.itemType === "template") {
          const resolvedUri = await this.resolveResourceTemplate(selected.value);
          if (!resolvedUri) return;
          uri = resolvedUri;
        }

        const result = await this.provider.readMcpResource(uri, 30000);
        const doc = await vscode.workspace.openTextDocument({
          language: "json",
          content:
            typeof result === "string"
              ? result
              : JSON.stringify(result, null, 2),
        });
        await vscode.window.showTextDocument(doc, { preview: false });
      }),

      vscode.commands.registerCommand("nexus-ide.runMcpPrompt", async () => {
        const snapshot = this.context.globalState.get<DiscoverySnapshot>(
          STATE_KEY_MCP_DISCOVERY,
        );
        if (!snapshot) {
          vscode.window.showWarningMessage(
            "Nenhum snapshot MCP encontrado. Aguarde o discovery ou reinicie a extensao.",
          );
          return;
        }

        const selected = await vscode.window.showQuickPick(
          (snapshot.prompts ?? []).map((prompt) => ({
            label: prompt.name,
            detail: prompt.description,
            prompt,
          })),
          { placeHolder: "Selecione um MCP prompt" },
        );
        if (!selected) return;

        const args = await this.collectPromptArguments(selected.prompt);
        if (args === undefined) return;

        const response = await this.provider.getMcpPrompt(
          selected.prompt.name,
          args,
          30000,
        );
        const messages = Array.isArray(response?.messages) ? response.messages : [];
        const content = messages
          .map((message: any, index: number) => {
            const text =
              typeof message?.content?.text === "string"
                ? message.content.text
                : JSON.stringify(message?.content ?? {}, null, 2);
            return `# Message ${index + 1}\n\n${text}`;
          })
          .join("\n\n");

        const doc = await vscode.workspace.openTextDocument({
          language: "markdown",
          content: content || JSON.stringify(response, null, 2),
        });
        await vscode.window.showTextDocument(doc, { preview: false });
      }),

      vscode.commands.registerCommand("nexus-ide.refreshTree", () => {
        this.provider.clearDirCache();
        this.treeProvider.refresh();
        this.contextManager.setStatusBarMessage(
          "$(refresh) Nexus IDE: Tree refreshed",
          3000,
        );
      }),

      vscode.commands.registerCommand(
        "nexus-ide.refreshDiagnostics",
        async () => {
          await this.diagnosticProvider.refreshAll();
        },
      ),

      vscode.commands.registerCommand("nexus-ide.forceSave", async () => {
        const editor = vscode.window.activeTextEditor;
        let targetUri = editor?.document.uri;
        if (!targetUri || targetUri.scheme !== GX_SCHEME) {
          const visibleGxEditor = vscode.window.visibleTextEditors.find(
            (e) => e.document.uri.scheme === GX_SCHEME
          );
          if (visibleGxEditor) targetUri = visibleGxEditor.document.uri;
        }
        if (!targetUri || targetUri.scheme !== GX_SCHEME) return;
        const uri = targetUri;
        const activeEditor = vscode.window.visibleTextEditors.find(e => e.document.uri.toString() === uri.toString());
        if (!activeEditor) return;
        const content = Buffer.from(activeEditor.document.getText(), "utf8");

        await vscode.window.withProgress(
          {
            location: vscode.ProgressLocation.Notification,
            title: `GeneXus: Salvando ${uri.fsPath}...`,
            cancellable: false,
          },
          async () => {
            try {
              await this.provider.triggerSave(uri, content);
              this.contextManager.setStatusBarMessage(
                `$(check) Salvo: ${uri.fsPath}`,
                DEFAULT_STATUS_BAR_TIMEOUT,
              );
            } catch (e) {
              vscode.window.showErrorMessage(`Erro ao salvar: ${e}`);
            }
          },
        );
      }),

      vscode.commands.registerCommand(
        "gx.showReferences",
        async (objName: string) => {
          const activeEditor = vscode.window.activeTextEditor;
          if (!activeEditor) return;
          await vscode.commands.executeCommand(
            "editor.action.showReferences",
            activeEditor.document.uri,
            new vscode.Position(0, 0),
            [],
          );
        },
      ),

      vscode.commands.registerCommand("nexus-ide.viewHistory", (u) =>
        HistoryView.show(u, this.provider, this.historyProvider),
      ),
      vscode.commands.registerCommand("nexus-ide.generateDiagram", (u) =>
        DiagramView.show(u, this.provider),
      ),
      vscode.commands.registerCommand(
        "nexus-ide.showProperties",
        async (uri?: vscode.Uri, controlName?: string) => {
          const targetUri = uri || GxUriParser.getActiveGxUri();
          if (!targetUri) return;

          const info = GxUriParser.parse(targetUri);
          if (!info) return;

          const target = `${info.type}:${info.name}`;
          await PropertiesView.show(target, controlName || null, this.provider);
        },
      ),

      vscode.commands.registerCommand("nexus-ide.copyMcpConfig", async () => {
        const rootPath = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
        const defaultPath = rootPath ? `${rootPath}\\publish\\start_mcp.bat` : "C:\\Projetos\\GenexusMCP\\publish\\start_mcp.bat";
        const snippet = JSON.stringify(
          {
            mcpServers: {
              genexus18: {
                command: defaultPath.replace(/\\/g, "\\\\"),
                args: [],
              },
            },
          },
          null,
          2,
        );
        await vscode.env.clipboard.writeText(snippet);
        vscode.window.showInformationMessage(
          "MCP Configuration snippet for Claude/Cursor copied to clipboard (Local StdIO mode)!",
        );
      }),

      vscode.commands.registerCommand(
        "nexus-ide.runTest",
        async (item?: GxTreeItem) => {
          let objName = item?.gxName;
          if (!objName) {
            const editor = vscode.window.activeTextEditor;
            if (editor && editor.document.uri.scheme === GX_SCHEME) {
              objName = GxUriParser.getObjectName(editor.document.uri);
            }
          }
          if (!objName) return;

          const outputChannel =
            vscode.window.createOutputChannel("GeneXus Test");
          outputChannel.show();
          outputChannel.appendLine(`[GXtest] Running tests for: ${objName}...`);

          await vscode.window.withProgress(
            {
              location: vscode.ProgressLocation.Notification,
              title: `Running GXtest: ${objName}...`,
              cancellable: false,
            },
            async () => {
              const result = await this.provider.callMcpTool(
                "genexus_test",
                {
                  name: objName,
                },
                300000,
              );
              if (result && result.status === "Success") {
                outputChannel.appendLine(result.output || "Test passed!");
                vscode.window.showInformationMessage(`Test ${objName} PASSED!`);
              } else {
                outputChannel.appendLine(
                  result?.output || result?.error || "Test failed.",
                );
                vscode.window.showErrorMessage(
                  `Test ${objName} FAILED. Check output.`,
                );
              }
            },
          );
        },
      ),

      vscode.commands.registerCommand(
        "nexus-ide.runLinter",
        async (item?: GxTreeItem) => {
          let objName = item?.gxName;
          if (!objName) {
            const targetUri = GxUriParser.getActiveGxUri();
            if (targetUri) {
              objName = GxUriParser.getObjectName(targetUri);
            }
          }
          if (!objName) return;

          await vscode.window.withProgress(
            {
              location: vscode.ProgressLocation.Notification,
              title: `Running Performance Linter: ${objName}...`,
              cancellable: false,
            },
            async () => {
              await this.diagnosticProvider.refreshAll();
              vscode.window.showInformationMessage(
                `Linter completed for ${objName}. Check Problems tab.`,
              );
            },
          );
        },
      ),

      vscode.commands.registerCommand(
        "nexus-ide.extractProcedure",
        async () => {
          const editor = vscode.window.activeTextEditor;
          let activeDoc = editor?.document;
          
          if (!activeDoc || activeDoc.uri.scheme !== GX_SCHEME) {
            const visibleEditor = vscode.window.visibleTextEditors.find(e => e.document.uri.scheme === GX_SCHEME);
            activeDoc = visibleEditor?.document;
          }
          
          if (!activeDoc || !editor) return;

          const selection = editor.selection;
          const code = activeDoc.getText(selection);
          if (!code) {
            vscode.window.showErrorMessage(
              "Selecione um bloco de código para extrair.",
            );
            return;
          }

          const procName = await vscode.window.showInputBox({
            prompt: "Nome do novo Procedimento:",
            placeHolder: "e.g. CalculateTax",
          });
          if (!procName) return;

          await vscode.window.withProgress(
            {
              location: vscode.ProgressLocation.Notification,
              title: `Extracting to ${procName}...`,
              cancellable: false,
            },
            async () => {
              const sourceName = activeDoc!.uri.path
                .split("/")
                .pop()
                ?.replace(".gx", "");
              const result = await this.provider.callMcpTool(
                "genexus_refactor",
                {
                  action: "ExtractProcedure",
                  objectName: sourceName,
                  code,
                  procedureName: procName,
                },
                300000,
              );

              if (result && result.status === "Success") {
                vscode.window.showInformationMessage(
                  `Procedure '${procName}' created and call injected!`,
                );
                await vscode.commands.executeCommand("nexus-ide.refreshTree");
                await vscode.commands.executeCommand(
                  "workbench.action.files.save",
                );
              } else {
                vscode.window.showErrorMessage(
                  `Extraction failed: ${result?.error || "Unknown error"}`,
                );
              }
            },
          );
        },
      ),

      vscode.commands.registerCommand("nexus-ide.autoFix", async () => {
        const editor = vscode.window.activeTextEditor;
        let activeDoc = editor?.document;
        
        if (!activeDoc || activeDoc.uri.scheme !== GX_SCHEME) {
          const visibleEditor = vscode.window.visibleTextEditors.find(e => e.document.uri.scheme === GX_SCHEME);
          activeDoc = visibleEditor?.document;
        }

        if (!activeDoc) {
          vscode.window.showErrorMessage(
            "Abra um objeto GeneXus para usar o Auto-Fix.",
          );
          return;
        }

        const diagnostics = vscode.languages.getDiagnostics(activeDoc.uri);
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
              const result = await this.provider.callMcpTool(
                "genexus_explain_code",
                {
                  name: activeDoc.uri.path
                    .split("/")
                    .pop()
                    ?.replace(".gx", ""),
                  code: JSON.stringify({
                    error: error.message,
                    line: error.range.start.line,
                    code: activeDoc.getText(),
                  }),
                },
                60000,
              );

              if (result && result.fix) {
                const choice = await vscode.window.showInformationMessage(
                  `AI Fix suggested: ${result.summary}\nApply fix?`,
                  "Apply Fix",
                  "Cancel",
                );
                if (choice === "Apply Fix") {
                  const edit = new vscode.WorkspaceEdit();
                  const fullRange = new vscode.Range(
                    activeDoc.positionAt(0),
                    activeDoc.positionAt(
                      activeDoc.getText().length,
                    ),
                  );
                  edit.replace(activeDoc.uri, fullRange, result.fix);
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

  private async resolveResourceTemplate(template: string): Promise<string | undefined> {
    let resolved = template;
    const placeholders = [...template.matchAll(/\{([^}]+)\}/g)].map(
      (match) => match[1],
    );

    for (const placeholder of placeholders) {
      const value = await vscode.window.showInputBox({
        prompt: `Informe o valor para '${placeholder}'`,
        value: placeholder === "part" ? "Source" : undefined,
      });
      if (!value) {
        return undefined;
      }

      resolved = resolved.replace(`{${placeholder}}`, value);
    }

    return resolved;
  }

  private async collectPromptArguments(prompt: any): Promise<any | undefined> {
    const args: Record<string, string> = {};
    const promptArgs = Array.isArray(prompt?.arguments) ? prompt.arguments : [];

    for (const arg of promptArgs) {
      const value = await vscode.window.showInputBox({
        prompt: arg.description || `Informe o valor para '${arg.name}'`,
        placeHolder: arg.name,
      });

      if (value === undefined) {
        return undefined;
      }

      if (value || arg.required) {
        args[arg.name] = value;
      }
    }

    return args;
  }
}
