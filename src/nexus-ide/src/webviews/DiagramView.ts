import * as vscode from "vscode";
import { GxFileSystemProvider } from "../gxFileSystem";
import { GxTreeItem } from "../gxTreeProvider";

export class DiagramView {
  private static panels = new Map<string, vscode.WebviewPanel>();

  static async show(item: GxTreeItem | undefined, provider: GxFileSystemProvider) {
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
      vscode.window.showErrorMessage("Selecione um objeto para gerar o diagrama.");
      return;
    }

    if (this.panels.has(objName)) {
        this.panels.get(objName)!.reveal(vscode.ViewColumn.Beside);
        return;
    }

    const panel = vscode.window.createWebviewPanel(
      "gxDiagram",
      `${objName} Diagram`,
      vscode.ViewColumn.Beside,
      { enableScripts: true }
    );

    this.panels.set(objName, panel);
    panel.onDidDispose(() => this.panels.delete(objName));

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
        panel.webview.html = "<h1>Não foi possível gerar o diagrama para este objeto.</h1>";
      }
    } catch (e) {
      panel.webview.html = `<h1>Erro: ${e}</h1>`;
    }
  }
}
