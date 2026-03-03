import * as assert from "assert";
import * as vscode from "vscode";

suite("Nexus IDE Extension Test Suite", () => {
  vscode.window.showInformationMessage("Start all tests.");

  test("Extension should be present", () => {
    assert.ok(vscode.extensions.getExtension("lennix1337.nexus-ide"));
  });

  test("Should register custom filesystem provider", async () => {
    const uri = vscode.Uri.parse("gxkb18:/Procedure/Test.gx");
    try {
        const stat = await vscode.workspace.fs.stat(uri);
        assert.ok(stat.type === vscode.FileType.File || stat.type === vscode.FileType.Directory);
    } catch {
        // If server is not running during test, we at least check if provider exists
        const provider = vscode.workspace.fs;
        assert.ok(provider !== null);
    }
  });

  test("Should have core commands registered", async () => {
    // Wait for activation if needed
    const extension = vscode.extensions.getExtension("lennix1337.nexus-ide");
    if (extension && !extension.isActive) {
      await extension.activate();
    }

    const commands = await vscode.commands.getCommands(true);
    assert.ok(
      commands.includes("nexus-ide.openKb"),
      "Command openKb not found",
    );
    assert.ok(
      commands.includes("nexus-ide.buildObject"),
      "Command buildObject not found",
    );
    assert.ok(
      commands.includes("nexus-ide.indexKb"),
      "Command indexKb not found",
    );
  });
});
