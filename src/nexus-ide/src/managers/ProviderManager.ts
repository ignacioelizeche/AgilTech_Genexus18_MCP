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
import { TYPE_SUFFIX } from "../utils/GxPartMapper";
import { GX_SCHEME } from "../constants";

export class ProviderManager {
  public historyProvider: any;

  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly provider: GxFileSystemProvider,
  ) {}

  register() {
    this.context.subscriptions.push(
      vscode.languages.registerDocumentSymbolProvider(
        "genexus",
        new GxDocumentSymbolProvider(),
      ),
      vscode.languages.registerDefinitionProvider(
        "genexus",
        new GxDefinitionProvider(this.provider),
      ),
      vscode.languages.registerHoverProvider(
        "genexus",
        new GxHoverProvider(this.provider),
      ),
      vscode.languages.registerCompletionItemProvider(
        "genexus",
        new GxCompletionItemProvider(this.provider, (uri) =>
          this.provider.getPart(uri),
        ),
        ".",
        "&",
        ":",
      ),
      vscode.languages.registerInlineCompletionItemProvider(
        "genexus",
        new GxInlineCompletionItemProvider(),
      ),
      vscode.languages.registerSignatureHelpProvider(
        "genexus",
        new GxSignatureHelpProvider(this.provider),
        "(",
        ",",
      ),
      vscode.languages.registerCodeActionsProvider(
        "genexus",
        new GxCodeActionProvider(),
        {
          providedCodeActionKinds: [GxCodeActionProvider.kind],
        },
      ),
      vscode.languages.registerRenameProvider(
        "genexus",
        new GxRenameProvider(this.provider),
      ),
      vscode.languages.registerDocumentFormattingEditProvider(
        "genexus",
        new GxFormatProvider(this.provider),
      ),
      vscode.languages.registerWorkspaceSymbolProvider(
        new GxWorkspaceSymbolProvider(this.provider),
      ),
      vscode.languages.registerCodeLensProvider(
        "genexus",
        new GxCodeLensProvider(this.provider),
      ),
      vscode.languages.registerReferenceProvider(
        "genexus",
        new GxReferenceProvider(this.provider),
      ),
    );

    this.registerHistoryProvider();
    this.registerFileSearchProvider();
  }

  private registerHistoryProvider() {
    this.historyProvider = new (class
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
    this.context.subscriptions.push(
      vscode.workspace.registerTextDocumentContentProvider(
        "gx-history",
        this.historyProvider,
      ),
    );
  }

  private registerFileSearchProvider() {
    if (!(vscode.workspace as any).registerFileSearchProvider) {
      return;
    }

    try {
      (vscode.workspace as any).registerFileSearchProvider(GX_SCHEME, {
        provideFileSearchResults: async (
          query: any,
          _options: any,
          token: vscode.CancellationToken,
        ): Promise<vscode.Uri[]> => {
          try {
            const pattern = query.pattern || "";
            if (pattern.length < 2) return [];

            const result = await this.provider.queryObjects(
              pattern + " @quick",
              50,
              5000,
            );

            if (token.isCancellationRequested) return [];

            if (result && result.results) {
              return result.results.map((obj: any) => {
                const suffix = TYPE_SUFFIX[obj.type]
                  ? `.${TYPE_SUFFIX[obj.type]}`
                  : "";
                return vscode.Uri.parse(
                  `${GX_SCHEME}:/${obj.type}/${obj.name}${suffix}.gx`,
                );
              });
            }
          } catch (e) {
            console.error("[ProviderManager] File search failed:", e);
          }
          return [];
        },
      });
    } catch (e) {
      console.warn("[ProviderManager] File search provider unavailable:", e);
    }
  }
}
