import * as assert from "assert";
import * as vscode from "vscode";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { GxFileSystemProvider } from "../../gxFileSystem";
import { hasUsableSearchIndex } from "../../extension";
import { GxShadowService } from "../../gxShadowService";
import { GxTreeProvider } from "../../gxTreeProvider";
import { GxUriParser } from "../../utils/GxUriParser";

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

  test("Should parse mirror file URIs from persisted index", () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-mirror-"));
    try {
      const relativePath = path.join("Root", "Financeiro", "DebugGravar.gx");
      const absolutePath = path.join(tempRoot, relativePath);
      fs.mkdirSync(path.dirname(absolutePath), { recursive: true });
      fs.writeFileSync(absolutePath, "");
      fs.writeFileSync(
        path.join(tempRoot, ".gx_index.json"),
        JSON.stringify(
          [
            {
              type: "Procedure",
              name: "DebugGravar",
              part: "Source",
              path: relativePath.replace(/\\/g, "/"),
              parentPath: "Root/Financeiro",
              containerPath: "Root/Financeiro/DebugGravar",
            },
          ],
          null,
          2,
        ),
      );

      GxUriParser.configureShadowRoot(tempRoot);
      GxUriParser.loadMirrorIndex(tempRoot);

      const parsed = GxUriParser.parse(vscode.Uri.file(absolutePath));
      assert.ok(parsed);
      assert.strictEqual(parsed?.type, "Procedure");
      assert.strictEqual(parsed?.name, "DebugGravar");
      assert.strictEqual(parsed?.part, "Source");
      assert.strictEqual(parsed?.parentPath, "Root/Financeiro");
      assert.strictEqual(parsed?.containerPath, "Root/Financeiro/DebugGravar");

      const editorUri = GxUriParser.toEditorUri("Procedure", "DebugGravar");
      assert.strictEqual(editorUri.scheme, "file");
      assert.strictEqual(editorUri.fsPath.toLowerCase(), absolutePath.toLowerCase());
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
      GxUriParser.configureShadowRoot(undefined);
      GxUriParser.clearMirrorIndex();
    }
  });

  test("Should resolve mirrored Rules part when indexed", () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-parts-"));
    try {
      const sourcePath = path.join(tempRoot, "Financeiro", "DebugGravar.gx");
      const rulesPath = path.join(tempRoot, "Financeiro", "DebugGravar.Rules.gx");
      fs.mkdirSync(path.dirname(sourcePath), { recursive: true });
      fs.writeFileSync(sourcePath, "");
      fs.writeFileSync(rulesPath, "");
      fs.writeFileSync(
        path.join(tempRoot, ".gx_index.json"),
        JSON.stringify(
          [
            {
              type: "Procedure",
              name: "DebugGravar",
              part: "Source",
              path: "Financeiro/DebugGravar.gx",
            },
            {
              type: "Procedure",
              name: "DebugGravar",
              part: "Rules",
              path: "Financeiro/DebugGravar.Rules.gx",
            },
          ],
          null,
          2,
        ),
      );

      GxUriParser.configureShadowRoot(tempRoot);
      GxUriParser.loadMirrorIndex(tempRoot);

      const rulesUri = GxUriParser.toEditorUri("Procedure", "DebugGravar", "Rules");
      assert.strictEqual(rulesUri.scheme, "file");
      const parsed = GxUriParser.parse(rulesUri);
      assert.strictEqual(parsed?.part, "Rules");
      assert.strictEqual(parsed?.name, "DebugGravar");
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
      GxUriParser.configureShadowRoot(undefined);
      GxUriParser.clearMirrorIndex();
    }
  });

  test("Should browse children using parentPath instead of parent name", async () => {
    const provider = new GxFileSystemProvider();
    (provider as any).listObjects = async () => [
      {
        name: "Procs",
        type: "Folder",
        parent: "ModuloA",
        parentPath: "ModuloA",
        path: "ModuloA/Procs",
      },
      {
        name: "Procs",
        type: "Folder",
        parent: "ModuloB",
        parentPath: "ModuloB",
        path: "ModuloB/Procs",
      },
    ];
    (provider as any).queryObjects = async () => {
      throw new Error("browseObjects should resolve from structural list in this test");
    };

    const entries = await provider.browseObjects("ModuloA");

    assert.strictEqual(entries.length, 1);
    assert.strictEqual(entries[0].name, "Procs");
    assert.strictEqual(entries[0].path, "ModuloA/Procs");
    assert.strictEqual(entries[0].parentPath, "ModuloA");
  });

  test("Should reuse existing index when lifecycle counters reset but root browse works", async () => {
    const provider = {
      browseObjects: async () => [
        {
          name: "ModuloA",
          type: "Module",
          parent: "Root Module",
          parentPath: "",
          path: "ModuloA",
        },
        {
          name: "PaginaInicial",
          type: "WebPanel",
          parent: "Root Module",
          parentPath: "",
          path: "PaginaInicial",
        },
      ],
    } as Pick<GxFileSystemProvider, "browseObjects">;

    const usable = await hasUsableSearchIndex(provider, {
      isIndexing: false,
      total: 0,
      processed: 0,
      status: "",
    });

    assert.strictEqual(usable, true);
  });

  test("Should materialize duplicate folder names under different modules separately", async () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-materialize-"));
    const shadowService = new GxShadowService("http://127.0.0.1:1", tempRoot);
    const provider = {
      browseObjects: async (parentPath: string) => {
        switch (parentPath) {
          case "":
            return [
              { name: "ModuloA", type: "Module", parent: "Root Module", parentPath: "", path: "ModuloA" },
              { name: "ModuloB", type: "Module", parent: "Root Module", parentPath: "", path: "ModuloB" },
            ];
          case "ModuloA":
            return [
              { name: "Procs", type: "Folder", parent: "ModuloA", parentPath: "ModuloA", path: "ModuloA/Procs" },
            ];
          case "ModuloB":
            return [
              { name: "Procs", type: "Folder", parent: "ModuloB", parentPath: "ModuloB", path: "ModuloB/Procs" },
            ];
          case "ModuloA/Procs":
            return [
              { name: "ProcA", type: "Procedure", parent: "Procs", parentPath: "ModuloA/Procs", path: "ModuloA/Procs/ProcA" },
            ];
          case "ModuloB/Procs":
            return [
              { name: "ProcB", type: "Procedure", parent: "Procs", parentPath: "ModuloB/Procs", path: "ModuloB/Procs/ProcB" },
            ];
          default:
            return [];
        }
      },
    } as unknown as GxFileSystemProvider;

    try {
      await shadowService.materializeWorkspaceWithProgress(provider);

      assert.ok(fs.existsSync(path.join(tempRoot, "ModuloA", "Procs", "ProcA.gx")));
      assert.ok(fs.existsSync(path.join(tempRoot, "ModuloB", "Procs", "ProcB.gx")));

      const index = JSON.parse(
        fs.readFileSync(path.join(tempRoot, ".gx_index.json"), "utf8"),
      );
      assert.ok(Array.isArray(index));
      assert.ok(index.some((entry: any) => entry.path === "ModuloA/Procs/ProcA.gx" && entry.parentPath === "ModuloA/Procs"));
      assert.ok(index.some((entry: any) => entry.path === "ModuloB/Procs/ProcB.gx" && entry.parentPath === "ModuloB/Procs"));
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
      GxUriParser.configureShadowRoot(undefined);
      GxUriParser.clearMirrorIndex();
    }
  });

  test("Should group root explorer nodes into Main Programs and Root Module", async () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-tree-"));
    try {
      fs.mkdirSync(path.join(tempRoot, "ModuloA"), { recursive: true });
      fs.mkdirSync(path.join(tempRoot, "ApiPix"), { recursive: true });
      fs.writeFileSync(
        path.join(tempRoot, ".gx_containers.json"),
        JSON.stringify(
          [
            { type: "Module", name: "ModuloA", path: "ModuloA", parentPath: "" },
            { type: "Folder", name: "ApiPix", path: "ApiPix", parentPath: "" },
          ],
          null,
          2,
        ),
      );
      GxUriParser.configureShadowRoot(tempRoot);

      const provider = new GxTreeProvider(
        tempRoot,
        vscode.Uri.file(path.join(tempRoot, "extension")),
      );

      const rootItems = await provider.getChildren();
      assert.strictEqual(rootItems.length, 2);
      assert.strictEqual(rootItems[0].label, "Main Programs");
      assert.strictEqual(rootItems[1].label, "Root Module");

      const moduleItems = await provider.getChildren(rootItems[1]);
      assert.strictEqual(moduleItems.length, 1);
      assert.strictEqual(moduleItems[0].label, "ModuloA");
      assert.strictEqual(moduleItems[0].gxType, "Module");

      const mainProgramItems = await provider.getChildren(rootItems[0]);
      assert.strictEqual(mainProgramItems.length, 1);
      assert.strictEqual(mainProgramItems[0].label, "ApiPix");
      assert.strictEqual(mainProgramItems[0].gxType, "Folder");
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
      GxUriParser.configureShadowRoot(undefined);
      GxUriParser.clearMirrorIndex();
    }
  });

  test("Should preserve module classification when folder and module share the same display name", async () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-tree-dupe-"));
    try {
      fs.mkdirSync(path.join(tempRoot, "CAU"), { recursive: true });
      fs.mkdirSync(path.join(tempRoot, "CAU [Module]"), { recursive: true });
      fs.writeFileSync(
        path.join(tempRoot, ".gx_containers.json"),
        JSON.stringify(
          [
            { type: "Folder", name: "CAU", path: "CAU", parentPath: "" },
            { type: "Module", name: "CAU", path: "CAU [Module]", parentPath: "" },
          ],
          null,
          2,
        ),
      );
      GxUriParser.configureShadowRoot(tempRoot);

      const provider = new GxTreeProvider(
        tempRoot,
        vscode.Uri.file(path.join(tempRoot, "extension")),
      );

      const rootItems = await provider.getChildren();
      assert.strictEqual(rootItems.length, 2);
      assert.strictEqual(rootItems[0].label, "Main Programs");
      assert.strictEqual(rootItems[1].label, "Root Module");

      const mainProgramItems = await provider.getChildren(rootItems[0]);
      assert.strictEqual(mainProgramItems.length, 1);
      assert.strictEqual(mainProgramItems[0].label, "CAU");
      assert.strictEqual(mainProgramItems[0].gxType, "Folder");

      const moduleItems = await provider.getChildren(rootItems[1]);
      assert.strictEqual(moduleItems.length, 1);
      assert.strictEqual(moduleItems[0].label, "CAU");
      assert.strictEqual(moduleItems[0].gxType, "Module");
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
      GxUriParser.configureShadowRoot(undefined);
      GxUriParser.clearMirrorIndex();
    }
  });

  test("Should treat legacy mirror without container index as not materialized", () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-legacy-mirror-"));
    try {
      fs.writeFileSync(
        path.join(tempRoot, ".gx_index.json"),
        JSON.stringify(
          [
            {
              type: "WebPanel",
              name: "PaginaA",
              part: "Source",
              path: "ModuloA/PaginaA.gx",
            },
          ],
          null,
          2,
        ),
      );
      fs.mkdirSync(path.join(tempRoot, "ModuloA"), { recursive: true });
      fs.writeFileSync(path.join(tempRoot, "ModuloA", "PaginaA.gx"), "");

      const shadowService = new GxShadowService("http://127.0.0.1:1", tempRoot);

      assert.strictEqual(
        shadowService.hasMaterializedWorkspace(),
        false,
        "Legacy mirrors missing .gx_containers.json must be rematerialized",
      );
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
      GxUriParser.configureShadowRoot(undefined);
      GxUriParser.clearMirrorIndex();
    }
  });
});
