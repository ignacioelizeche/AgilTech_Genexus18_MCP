import * as vscode from "vscode";
import * as fs from "fs";
import * as path from "path";
import { GxUriParser } from "./utils/GxUriParser";

const TYPE_ICON_FILE: Record<string, string> = {
  Module: "module",
  Folder: "folder",
  Procedure: "procedure",
  WebPanel: "webpanel",
  Transaction: "transaction",
  SDT: "sdt",
  StructuredDataType: "sdt",
  DataProvider: "dataprovider",
  DataView: "dataview",
  Attribute: "attribute",
  Table: "table",
  SDPanel: "sdpanel",
};

function getTypeFromResource(uri: vscode.Uri): string {
  if (uri.scheme === "file" && fs.existsSync(uri.fsPath) && fs.statSync(uri.fsPath).isDirectory()) {
    return "Folder";
  }

  const info = GxUriParser.parse(uri);
  return info?.type || "Object";
}

export class GxTreeItem extends vscode.TreeItem {
  constructor(
    public readonly resourceUri: vscode.Uri,
    collapsibleState: vscode.TreeItemCollapsibleState,
    private readonly extensionUri: vscode.Uri,
  ) {
    super(resourceUri, collapsibleState);

    const gxType = getTypeFromResource(resourceUri);
    const isContainer = collapsibleState !== vscode.TreeItemCollapsibleState.None;

    this.tooltip = `[${gxType}] ${path.basename(resourceUri.fsPath || resourceUri.path)}`;
    this.contextValue = `gx_${gxType.toLowerCase()}`;

    const iconFile = TYPE_ICON_FILE[gxType];
    if (iconFile) {
      const iconUri = vscode.Uri.joinPath(
        extensionUri,
        "resources",
        `${iconFile}.svg`,
      );
      this.iconPath = { light: iconUri, dark: iconUri };
    }

    if (!isContainer) {
      this.command = {
        command: "vscode.open",
        title: "Open",
        arguments: [this.resourceUri],
      };
    }
  }

  get gxName(): string {
    const parsed = GxUriParser.parse(this.resourceUri);
    return parsed?.name || path.basename(this.resourceUri.fsPath || this.resourceUri.path);
  }

  get gxType(): string {
    return getTypeFromResource(this.resourceUri);
  }

  get gxParentPath(): string {
    return path.dirname(this.resourceUri.fsPath || this.resourceUri.path);
  }
}

export class GxTreeProvider implements vscode.TreeDataProvider<GxTreeItem> {
  private _onDidChangeTreeData = new vscode.EventEmitter<
    GxTreeItem | undefined | null | void
  >();
  readonly onDidChangeTreeData = this._onDidChangeTreeData.event;
  private _cache = new Map<string, { items: GxTreeItem[]; time: number }>();

  constructor(
    private readonly shadowRoot: string,
    private readonly extensionUri: vscode.Uri,
  ) {}

  refresh(): void {
    this._cache.clear();
    this._onDidChangeTreeData.fire();
  }

  refreshNode(item: GxTreeItem): void {
    this._cache.delete(item.resourceUri.fsPath.toLowerCase());
    this._onDidChangeTreeData.fire(item);
  }

  getTreeItem(element: GxTreeItem): vscode.TreeItem {
    return element;
  }

  async getChildren(element?: GxTreeItem): Promise<GxTreeItem[]> {
    const targetDir = element ? element.resourceUri.fsPath : this.shadowRoot;
    const cacheKey = targetDir.toLowerCase();
    const cached = this._cache.get(cacheKey);
    if (cached && Date.now() - cached.time < 300000) {
      return cached.items;
    }

    if (!fs.existsSync(targetDir) || !fs.statSync(targetDir).isDirectory()) {
      return [];
    }

    const items = fs
      .readdirSync(targetDir, { withFileTypes: true })
      .filter((entry) =>
        entry.name !== ".gx_index.json" &&
        entry.name !== ".mcp_config.json",
      )
      .sort((a, b) => {
        if (a.isDirectory() !== b.isDirectory()) {
          return a.isDirectory() ? -1 : 1;
        }
        return a.name.localeCompare(b.name, undefined, { sensitivity: "base" });
      })
      .map((entry) => {
        const itemUri = vscode.Uri.file(path.join(targetDir, entry.name));
        return new GxTreeItem(
          itemUri,
          entry.isDirectory()
            ? vscode.TreeItemCollapsibleState.Collapsed
            : vscode.TreeItemCollapsibleState.None,
          this.extensionUri,
        );
      });

    this._cache.set(cacheKey, { items, time: Date.now() });
    return items;
  }
}
