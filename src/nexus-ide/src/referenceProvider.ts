import * as vscode from "vscode";
import { GX_SCHEME } from "./constants";
import { GxFileSystemProvider } from "./gxFileSystem";

export class GxReferenceProvider implements vscode.ReferenceProvider {
  constructor(private readonly provider: GxFileSystemProvider) {}

  async provideReferences(
    document: vscode.TextDocument,
    position: vscode.Position,
    context: vscode.ReferenceContext,
    _token: vscode.CancellationToken,
  ): Promise<vscode.Location[]> {
    if (document.uri.scheme !== GX_SCHEME) return [];

    const range = document.getWordRangeAtPosition(position);
    const word = document.getText(range);

    // Remove & if it's a variable reference (we don't support global variable search yet, searching for object uses)
    const targetName = word.startsWith("&") ? word.substring(1) : word;

    try {
      const results = await this.provider.queryObjects(`usedby:${targetName}`, 100, 15000);

      if (results && results.results) {
        return results.results.map((obj: any) => {
          return new vscode.Location(
            vscode.Uri.from({
              scheme: GX_SCHEME,
              path: `/${obj.type}/${obj.name}.gx`,
            }),
            new vscode.Position(0, 0),
          );
        });
      }
    } catch (e) {
      console.error("[Nexus IDE] Reference Provider error:", e);
    }

    return [];
  }
}
