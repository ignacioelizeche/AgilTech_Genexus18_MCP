import * as vscode from "vscode";
import { GxFileSystemProvider } from "../gxFileSystem";

export class LayoutView {
  private static panels = new Map<string, vscode.WebviewPanel>();

  static async show(uri: vscode.Uri, provider: GxFileSystemProvider) {
    const pathStr = decodeURIComponent(uri.path.substring(1));
    const parts = pathStr.split("/");
    const typeStr = parts.length > 1 ? parts[0] : null;
    const objName = parts.pop()!.replace(".gx", "");
    const uriKey = uri.toString();
    const target = typeStr ? `${typeStr}:${objName}` : objName;

    if (this.panels.has(uriKey)) {
      this.panels.get(uriKey)!.reveal(vscode.ViewColumn.Beside);
      return;
    }

    const panel = vscode.window.createWebviewPanel(
      "gxLayout",
      `${objName} Layout`,
      vscode.ViewColumn.Beside,
      { enableScripts: true, enableCommandUris: true }
    );

    this.panels.set(uriKey, panel);
    panel.onDidDispose(() => this.panels.delete(uriKey));

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
  }
}
