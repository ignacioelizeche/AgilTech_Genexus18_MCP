import * as vscode from "vscode";
import { GxFileSystemProvider } from "../gxFileSystem";
import { GxDocumentSymbolProvider } from "../symbolProvider";
import { GxDefinitionProvider } from "../definitionProvider";
import { GxHoverProvider } from "../hoverProvider";
import { GxCompletionItemProvider } from "../completionProvider";
import { GxInlineCompletionItemProvider } from "../inlineCompletionProvider";
import { GxSignatureHelpProvider } from "../signatureHelpProvider";
import { GxCodeActionProvider } from "../codeActionProvider";
import { GxRenameProvider } from "../renameProvider";
import { GxFormatProvider } from "../formatProvider";
import { GxWorkspaceSymbolProvider } from "../workspaceSymbolProvider";
import { GxCodeLensProvider } from "../codeLensProvider";
import { GxReferenceProvider } from "../referenceProvider";

export class ProviderManager {
  public historyProvider: any;

  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly provider: GxFileSystemProvider
  ) {}

  register() {
    const callGateway = (cmd: any) => this.provider.callGateway(cmd);

    this.context.subscriptions.push(
      vscode.languages.registerDocumentSymbolProvider("genexus", new GxDocumentSymbolProvider()),
      vscode.languages.registerDefinitionProvider("genexus", new GxDefinitionProvider(callGateway)),
      vscode.languages.registerHoverProvider("genexus", new GxHoverProvider(callGateway)),
      vscode.languages.registerCompletionItemProvider("genexus", new GxCompletionItemProvider(callGateway), ".", "&"),
      vscode.languages.registerInlineCompletionItemProvider("genexus", new GxInlineCompletionItemProvider()),
      vscode.languages.registerSignatureHelpProvider("genexus", new GxSignatureHelpProvider(callGateway), "(", ","),
      vscode.languages.registerCodeActionsProvider("genexus", new GxCodeActionProvider(callGateway), {
        providedCodeActionKinds: [GxCodeActionProvider.kind],
      }),
      vscode.languages.registerRenameProvider("genexus", new GxRenameProvider(callGateway)),
      vscode.languages.registerDocumentFormattingEditProvider("genexus", new GxFormatProvider(callGateway)),
      vscode.languages.registerWorkspaceSymbolProvider(new GxWorkspaceSymbolProvider(callGateway)),
      vscode.languages.registerCodeLensProvider("genexus", new GxCodeLensProvider(callGateway)),
      vscode.languages.registerReferenceProvider("genexus", new GxReferenceProvider(callGateway))
    );

    this.registerHistoryProvider();
    this.registerFileSearchProvider();
  }

  private registerHistoryProvider() {
    this.historyProvider = new (class implements vscode.TextDocumentContentProvider {
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
    this.context.subscriptions.push(
      vscode.workspace.registerTextDocumentContentProvider("gx-history", this.historyProvider)
    );
  }

  private registerFileSearchProvider() {
    if ((vscode.workspace as any).registerFileSearchProvider) {
      (vscode.workspace as any).registerFileSearchProvider("genexus", {
        provideFileSearchResults: async (query: any, _options: any, token: vscode.CancellationToken): Promise<vscode.Uri[]> => {
          try {
            const pattern = query.pattern || "";
            if (pattern.length < 2) return [];

            const result = await this.provider.callGateway({
              method: "execute_command",
              params: { module: "Search", target: pattern + " @quick", limit: 100 },
            });

            if (token.isCancellationRequested) return [];

            if (result && result.results) {
              return result.results.map((obj: any) =>
                vscode.Uri.parse(`genexus:/${obj.type}/${obj.name}.gx`)
              );
            }
          } catch (e) {
            console.error("[ProviderManager] File search failed:", e);
          }
          return [];
        },
      });
    }
  }
}
