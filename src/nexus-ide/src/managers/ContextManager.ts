import * as vscode from "vscode";
import { GxFileSystemProvider } from "../gxFileSystem";

export class ContextManager {
  private statusBarItem: vscode.StatusBarItem;

  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly provider: GxFileSystemProvider
  ) {
    this.statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
    this.context.subscriptions.push(this.statusBarItem);
  }

  register() {
    this.context.subscriptions.push(
      vscode.window.onDidChangeActiveTextEditor((editor) => {
        this.updateActiveContext(editor?.document.uri);
      })
    );

    // Initial update
    this.updateActiveContext(vscode.window.activeTextEditor?.document.uri);
  }

  updateActiveContext(uri?: vscode.Uri) {
    if (uri && uri.scheme === "genexus") {
      const part = this.provider.getPart(uri);
      const pathStr = decodeURIComponent(uri.path.substring(1));
      const objName = pathStr.split("/").pop()!.replace(".gx", "");

      vscode.commands.executeCommand("setContext", "genexus.activePart", part);

      this.statusBarItem.text = `$(file-code) GX: ${objName} > ${part}`;
      this.statusBarItem.show();
    } else {
      vscode.commands.executeCommand("setContext", "genexus.activePart", null);
      this.statusBarItem.hide();
    }
  }

  setStatusBarMessage(message: string, timeout: number = 5000) {
    vscode.window.setStatusBarMessage(message, timeout);
  }
}
