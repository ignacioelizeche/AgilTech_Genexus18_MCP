import * as vscode from "vscode";
import { GxFileSystemProvider } from "../gxFileSystem";
import { GxUriParser } from "../utils/GxUriParser";

export class HistoryView {
  private static panels = new Map<string, vscode.WebviewPanel>();

  static async show(
    uri: vscode.Uri | undefined,
    provider: GxFileSystemProvider,
    historyProvider: any,
  ) {
    let targetUri = uri || GxUriParser.getActiveGxUri();
    const info = targetUri ? GxUriParser.parse(targetUri) : null;
    let objName = info?.name || "";

    if (!objName) {
      vscode.window.showErrorMessage(
        "Abra ou selecione um objeto para ver o histórico.",
      );
      return;
    }

    const uriKey = targetUri?.toString() || objName;
    if (this.panels.has(uriKey)) {
      this.panels.get(uriKey)!.reveal(vscode.ViewColumn.Beside);
      return;
    }

    const panel = vscode.window.createWebviewPanel(
      "gxHistory",
      `Histórico: ${objName}`,
      vscode.ViewColumn.Beside,
      { enableScripts: true },
    );

    this.panels.set(uriKey, panel);
    panel.onDidDispose(() => {
      this.panels.delete(uriKey);
      historyProvider.clear(objName);
    });

    panel.webview.html = `<h1>Carregando Histórico de ${objName}...</h1>`;

    try {
      const result = await provider.callMcpTool("genexus_history", {
        action: "list",
        name: objName,
      });

      if (result && result.history) {
        let rows = "";
        if (Array.isArray(result.history)) {
          const sortedHistory = [...result.history].reverse();
          rows = sortedHistory
            .map(
              (rev: any) => `
                <tr>
                    <td style="padding: 10px; border-bottom: 1px solid #333; font-weight: bold; color: #007acc;">#${rev.version || rev.Id || ""}</td>
                    <td style="padding: 10px; border-bottom: 1px solid #333; white-space: nowrap;">${rev.date || rev.Date || ""}</td>
                    <td style="padding: 10px; border-bottom: 1px solid #333;">${rev.user || rev.User || ""}</td>
                    <td style="padding: 10px; border-bottom: 1px solid #333; font-style: italic; color: #aaa;">${rev.comment || rev.Comment || '<span style="opacity: 0.5;">Sem comentário</span>'}</td>
                    <td style="padding: 10px; border-bottom: 1px solid #333; text-align: center;">
                        <button onclick="viewDiff(${rev.version || rev.Id})" style="background: #007acc; color: white; border: none; padding: 4px 8px; cursor: pointer; border-radius: 2px;">Comparar (Diff)</button>
                    </td>
                </tr>
            `,
            )
            .join("");
        }

        panel.webview.html = this.getHtml(objName, rows);

        panel.webview.onDidReceiveMessage(async (message) => {
          if (message.command === "viewDiff") {
            vscode.window.setStatusBarMessage(
              `$(sync~spin) Buscando Revisão #${message.versionId} para Diff...`,
              3000,
            );
            try {
              const codeResult = await provider.callMcpTool("genexus_history", {
                action: "get_source",
                name: message.objName,
                versionId: message.versionId,
              });

              if (codeResult && codeResult.source) {
                const historicalContent = Buffer.from(
                  codeResult.source,
                  "base64",
                ).toString("utf8");
                const historyUri = vscode.Uri.parse(
                  `gx-history:/v${message.versionId}/${message.objName}.gx`,
                );
                historyProvider.update(historyUri, historicalContent);

                await vscode.commands.executeCommand(
                  "vscode.diff",
                  historyUri,
                  targetUri!,
                  `${message.objName} (Revisão #${message.versionId}) ↔ (Atual)`,
                );
              }
            } catch (e) {
              vscode.window.showErrorMessage(`Erro ao buscar versão: ${e}`);
            }
          }
        });
      } else {
        panel.webview.html = "<h1>Histórico não encontrado.</h1>";
      }
    } catch (e) {
      panel.webview.html = `<h1>Erro Crítico: ${e}</h1>`;
    }
  }

  private static getHtml(objName: string, rows: string): string {
    return `
      <!DOCTYPE html>
      <html>
      <head>
          <style>
              body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif; padding: 20px; background-color: #1e1e1e; color: #ccc; }
              table { width: 100%; border-collapse: collapse; margin-top: 20px; background: #252526; box-shadow: 0 4px 6px rgba(0,0,0,0.3); border-radius: 4px; overflow: hidden; }
              th { background-color: #333333; color: #fff; text-align: left; padding: 12px 10px; font-size: 0.9em; text-transform: uppercase; letter-spacing: 1px; }
              tr:hover { background-color: #2d2d2d; }
              h2 { color: #fff; border-bottom: 1px solid #444; padding-bottom: 10px; }
              .badge { background: #007acc; color: white; padding: 2px 6px; border-radius: 3px; font-size: 0.8em; margin-left: 10px; }
          </style>
      </head>
      <body>
          <h2>Histórico de Revisões: ${objName} <span class="badge">SDK Nativo</span></h2>
          <table>
              <thead>
                  <tr>
                      <th>Rev</th>
                      <th>Data / Hora</th>
                      <th>Autor</th>
                      <th>Comentário de Commit</th>
                      <th>Ações</th>
                  </tr>
              </thead>
              <tbody>${rows || '<tr><td colspan="5" style="padding: 20px; text-align: center;">Nenhuma revisão encontrada.</td></tr>'}</tbody>
          </table>
          <script>
              const vscode = acquireVsCodeApi();
              function viewDiff(vId) {
                  vscode.postMessage({ command: 'viewDiff', versionId: vId, objName: '${objName}' });
              }
          </script>
      </body>
      </html>
    `;
  }
}
