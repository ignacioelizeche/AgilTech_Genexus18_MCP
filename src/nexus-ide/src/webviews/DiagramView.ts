import * as vscode from "vscode";
import { GxFileSystemProvider } from "../gxFileSystem";
import { GxTreeItem } from "../gxTreeProvider";
import { GX_SCHEME } from "../constants";

export class DiagramView {
  private static panels = new Map<string, vscode.WebviewPanel>();

  static async show(item: GxTreeItem | undefined, provider: GxFileSystemProvider) {
    let objName = "";
    if (item && item.gxName) {
      objName = item.gxName;
    } else {
      const editor = vscode.window.activeTextEditor;
      let targetUri = editor?.document.uri;
      if (!targetUri || targetUri.scheme !== GX_SCHEME) {
        const visibleGxEditor = vscode.window.visibleTextEditors.find(
          (e) => e.document.uri.scheme === GX_SCHEME
        );
        if (visibleGxEditor) targetUri = visibleGxEditor.document.uri;
      }
      
      if (targetUri && targetUri.scheme === GX_SCHEME) {
        const pathStr = decodeURIComponent(targetUri.path.substring(1));
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
      const result = await provider.callMcpTool("genexus_doc", {
        action: "visualize",
        target: objName,
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
