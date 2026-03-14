import * as vscode from "vscode";
import * as fs from "fs";
import * as path from "path";
import { GxShadowService } from "./gxShadowService";
import { GxDiagnosticProvider } from "./diagnosticProvider";
import { GxGatewayClient } from "./infra/GxGatewayClient";
import { GxUriParser } from "./utils/GxUriParser";
import { GxPartMapper, TYPE_SUFFIX, VALID_TYPES } from "./utils/GxPartMapper";
import { GxCacheManager } from "./managers/GxCacheManager";
import { 
  GX_SCHEME, 
  DEFAULT_MCP_PORT, 
  MODULE_SEARCH, 
  MODULE_KB, 
  MODULE_HEALTH,
  DEFAULT_STATUS_BAR_TIMEOUT,
  ROOT_PARENT_NAME
} from "./constants";

export { TYPE_SUFFIX };

const BROWSE_QUERY_LIMIT = 5000;
const BROWSE_FALLBACK_TYPES = [
  "Folder",
  "Module",
  "Procedure",
  "Transaction",
  "WebPanel",
  "DataProvider",
  "SDT",
  "StructuredDataType",
  "Domain",
  "Image",
  "Table",
  "DataView",
  "Attribute",
  "SDPanel",
  "WorkPanel",
  "ExternalObject",
  "Menu",
];

export class GxFileSystemProvider implements vscode.FileSystemProvider {
  private _emitter = new vscode.EventEmitter<vscode.FileChangeEvent[]>();
  readonly onDidChangeFile: vscode.Event<vscode.FileChangeEvent[]> =
    this._emitter.event;

  private _gateway: GxGatewayClient;
  private _cache: GxCacheManager;
  private _shadowService?: GxShadowService;
  private _diagnosticProvider?: GxDiagnosticProvider;
  public isBulkIndexing: boolean = false;

  private _kbInitPromise: Promise<any> | null = null;
  private _pendingQueryRequests = new Map<string, Promise<any>>();

  constructor() {
    this._cache = new GxCacheManager();
    this._gateway = new GxGatewayClient(
      `http://127.0.0.1:${DEFAULT_MCP_PORT}/mcp`,
    );
  }

  public set baseUrl(value: string) {
    this._gateway.baseUrl = value;
  }
  public get baseUrl(): string {
    return this._gateway.baseUrl;
  }

  public setShadowService(service: GxShadowService) {
    this._shadowService = service;
    (this._gateway as any)._shadowService = service;
  }

  public setDiagnosticProvider(provider: GxDiagnosticProvider) {
    this._diagnosticProvider = provider;
  }

  public setPart(uri: vscode.Uri, partName: string) {
    this._cache.filePartState.set(uri.path, partName);
    this._cache.invalidatePartCache(uri.toString(), partName);
    this._cache.mtimes.set(uri.toString(), Date.now());
    this._emitter.fire([{ type: vscode.FileChangeType.Changed, uri }]);
  }

  public getPart(uri: vscode.Uri): string {
    return GxPartMapper.getPart(uri, this._cache.filePartState);
  }

  public async initKb() {
    if (this._kbInitPromise) return this._kbInitPromise;

    console.log("[Nexus IDE] Warming up KB...");
    try {
      await this.callMcpMethod("ping", undefined, 2000);
    } catch {}

    this._kbInitPromise = (async () => {
      let lastError: unknown;
      for (let attempt = 1; attempt <= 20; attempt++) {
        try {
          return await this.callMcpMethod("ping", undefined, 5000);
        } catch (error) {
          lastError = error;
          await new Promise((resolve) => setTimeout(resolve, 1500));
        }
      }

      throw lastError ?? new Error("Gateway did not respond to KB initialization ping.");
    })();

    this._kbInitPromise.then(() => {
      console.log("[Nexus IDE] KB SDK Init complete. Refreshing Root...");
      this._emitter.fire([
        {
          type: vscode.FileChangeType.Changed,
          uri: vscode.Uri.from({ scheme: GX_SCHEME, path: "/" }),
        },
      ]);
      setTimeout(() => this.shadowMetadata(), 1000);
    }).catch((error) => {
      console.error("[Nexus IDE] KB init failed:", error);
      this._kbInitPromise = null;
    });

    return this._kbInitPromise;
  }

  private async shadowMetadata() {
    try {
      const result = await this.queryObjects(
        "type:Transaction or type:SDT",
        5,
        30000,
      );

      if (result && result.results) {
        for (const obj of result.results) {
          const suffix = TYPE_SUFFIX[obj.type]
            ? `.${TYPE_SUFFIX[obj.type]}`
            : "";
          const uri = vscode.Uri.parse(
            `${GX_SCHEME}:/${obj.type}/${obj.name}${suffix}.gx`,
          );
          const part = obj.type === "Transaction" ? "Structure" : "Source";
          void this.fetchAndCacheMetadata(uri, obj.type, obj.name, part);
          await new Promise((resolve) => setTimeout(resolve, 150));
        }
      }
    } catch (e) {
      console.error("[Nexus IDE] Shadowing failed:", e);
    }
  }

  private async fetchAndCacheMetadata(
    uri: vscode.Uri,
    objType: string,
    objName: string,
    partName: string,
  ) {
    try {
      const target = VALID_TYPES.has(objType)
        ? `${objType}:${objName}`
        : objName;
      const res = await this.callMcpTool(
        "genexus_read",
        {
          name: target,
          part: partName,
        },
        15000,
      );
      if (res && res.source) {
        let decoded = res.isBase64
          ? Buffer.from(res.source, "base64").toString("utf8")
          : res.source;
        this._cache.metadataCache.set(uri.toString() + ":" + partName, decoded);
      }
    } catch {}
  }

  watch(
    _uri: vscode.Uri,
    _options: { recursive: boolean; excludes: string[] },
  ): vscode.Disposable {
    return new vscode.Disposable(() => {});
  }

  stat(uri: vscode.Uri): vscode.FileStat {
    console.log(`[GxFS] stat: ${uri.toString()}`);
    const info = GxUriParser.parse(uri);
    const pathStr = info ? info.path : "";

    // Support IDE metadata probes (VS Code / Antigravity)
    if (
      info?.path === ".vscode" ||
      info?.path === ".mcp" ||
      info?.path === ".antigravity"
    ) {
      return {
        type: vscode.FileType.Directory,
        ctime: Date.now(),
        mtime: Date.now(),
        size: 0,
      };
    }
    if (
      info?.path.includes("mcp.json") ||
      info?.path.includes("tasks.json") ||
      info?.path.includes("settings.json")
    ) {
      return {
        type: vscode.FileType.File,
        ctime: Date.now(),
        mtime: Date.now(),
        size: 0,
      };
    }

    if (info?.path.startsWith(".") && !info?.path.startsWith(".gx")) {
      throw vscode.FileSystemError.FileNotFound(uri);
    }

    if (info?.path === "" || !info?.path.endsWith(".gx")) {
      return {
        type: vscode.FileType.Directory,
        ctime: Date.now(),
        mtime: Date.now(),
        size: 0,
      };
    }

    const cachedContent = this._cache.contentCache.get(uri.toString());
    let size = cachedContent ? cachedContent.byteLength : 0;

    if (size === 0) {
      const info = GxUriParser.parse(uri);
      if (info) {
        const shadowPath = path.join(
          this._shadowService?.shadowRoot || "",
          info.type,
          `${info.name}.gx`,
        );
        size = fs.existsSync(shadowPath) ? fs.statSync(shadowPath).size : 0;
      }
    }

    if (!this._cache.mtimes.has(uri.toString()))
      this._cache.mtimes.set(uri.toString(), Date.now());

    return {
      type: vscode.FileType.File,
      ctime: Date.now(),
      mtime: this._cache.mtimes.get(uri.toString())!,
      size: size,
    };
  }

  async readDirectory(uri: vscode.Uri): Promise<[string, vscode.FileType][]> {
    try {
      console.log(`[GxFS] readDirectory START: ${uri.toString()}`);
      const info = GxUriParser.parse(uri);
      const pathStr = info ? info.path : "";
      console.log(`[GxFS] readDirectory pathStr: "${pathStr}"`);

      const parentName = (info?.path === "" || !info)
        ? ROOT_PARENT_NAME
        : info.path.split("/").pop()!;
      const pathSegments = pathStr
        ? pathStr.split("/").filter((segment) => segment.length > 0)
        : [];
      const cacheKey = `dir:${pathStr}`;

      const cached = this._cache.dirCache.get(cacheKey);
      if (cached && Date.now() - cached.time < 300000) {
        console.log(`[GxFS] readDirectory CACHE HIT: ${cacheKey}`);
        return cached.entries;
      }

      console.log(`[GxFS] readDirectory Fetching: ${parentName}`);
      let objects: any[] = [];

      if (VALID_TYPES.has(parentName) && pathSegments.length > 0) {
        const result = await this.queryObjects(
          `type:${parentName} @quick`,
          BROWSE_QUERY_LIMIT,
          30000,
        );
        objects = Array.isArray(result?.results)
          ? result.results
          : Array.isArray(result)
            ? result
            : [];
      } else {
        objects = await this.browseObjects(parentName);
      }

      console.log(
        `[GxFS] readDirectory Gateway returned ${objects.length || 0} objects for ${parentName}`,
      );
      if (objects.length > 0) {
        console.log(
          `[GxFS] readDirectory First object example: ${JSON.stringify(objects[0]).substring(0, 100)}`,
        );
      }

      if (Array.isArray(objects)) {
        const mapped = objects.map((obj: any) => {
          const isDir = obj.type === "Folder" || obj.type === "Module";
          const suffix =
            !isDir && TYPE_SUFFIX[obj.type] ? `.${TYPE_SUFFIX[obj.type]}` : "";
          const name = isDir ? obj.name : `${obj.name}${suffix}.gx`;
          return [
            name,
            isDir ? vscode.FileType.Directory : vscode.FileType.File,
          ];
        }) as [string, vscode.FileType][];

        console.log(
          `[GxFS] readDirectory Mapped ${mapped.length} entries. Updating cache.`,
        );
        this._cache.dirCache.set(cacheKey, {
          entries: mapped,
          time: Date.now(),
        });
        return mapped;
      }
    } catch (e) {
      console.error(`[GxFS] readDirectory error for ${uri.toString()}:`, e);
    }
    return [];
  }

  async readFile(uri: vscode.Uri): Promise<Uint8Array> {
    const partName = this.getPart(uri);
    const uriStr = uri.toString();
    const cacheKey = this._cache.getCacheKey(uri, partName);

    const pCache = this._cache.partsCache.get(uriStr);
    if (pCache && pCache.has(partName)) return pCache.get(partName)!;

    const shadowed = this._cache.metadataCache.get(uriStr + ":" + partName);
    if (shadowed) return Buffer.from(shadowed, "utf8");

    if (this._cache.contentCache.has(uriStr))
      return this._cache.contentCache.get(uriStr)!;

    const cached = this._cache.readCache.get(cacheKey);
    if (cached && Date.now() - cached.time < 30000) return cached.data;

    if (this._cache.pendingReadRequests.has(cacheKey))
      return this._cache.pendingReadRequests.get(cacheKey)!;

    const request = (async () => {
      const info = GxUriParser.parse(uri);
      const target = info ? `${info.type}:${info.name}` : null;
      if (!target) {
        return Buffer.alloc(0);
      }
      try {
        const partsToFetch = Array.from(
          new Set(["Source", "Rules", "Events", "Variables", partName]),
        );
        const allPartsResult = await this.callMcpTool(
          "genexus_batch_read",
          {
            items: partsToFetch.map((part) => ({ name: target, part })),
          },
          30000,
        );
        if (allPartsResult && Array.isArray(allPartsResult.results)) {
          const newPCache = new Map<string, Uint8Array>();
          for (const partResult of allPartsResult.results) {
            const fetchedPart = partResult?.part || partResult?.requestedPart;
            const source = partResult?.source;
            if (typeof fetchedPart !== "string" || typeof source !== "string") {
              continue;
            }

            const partData = partResult?.isBase64
              ? Buffer.from(source, "base64")
              : Buffer.from(source, "utf8");
            newPCache.set(fetchedPart, partData);
          }
          this._cache.partsCache.set(uriStr, newPCache);
          if (newPCache.has(partName)) {
            const data = newPCache.get(partName)!;
            this._cache.contentCache.set(uriStr, data);
            this._shadowService?.syncToDisk(uri, data, partName);
            return data;
          }
        }

        const result = await this.callMcpTool(
          "genexus_read",
          {
            name: target,
            part: partName,
          },
          30000,
        );
        const data =
          result && result.source
            ? result.isBase64
              ? Buffer.from(result.source, "base64")
              : Buffer.from(result.source, "utf8")
            : Buffer.from(`// Part not available: ${partName}`, "utf8");

        this._cache.readCache.set(cacheKey, { data, time: Date.now() });
        this._cache.contentCache.set(uriStr, data);
        this._shadowService?.syncToDisk(uri, data, partName);
        return data;
      } catch (error) {
        return Buffer.from(`// Error reading part: ${error}`, "utf8");
      } finally {
        this._cache.pendingReadRequests.delete(cacheKey);
      }
    })();

    this._cache.pendingReadRequests.set(cacheKey, request);
    return request;
  }

  writeFile(
    uri: vscode.Uri,
    content: Uint8Array,
    options: { create: boolean; overwrite: boolean },
  ): Promise<void> {
    return this._writeFile(uri, content, options);
  }

  async preWarm(uri: vscode.Uri): Promise<void> {
    if (
      !this._cache.partsCache.has(uri.toString()) &&
      !this._cache.pendingReadRequests.has(uri.toString())
    ) {
      this.readFile(uri).catch(() => {});
    }
  }

  private async _writeFile(
    uri: vscode.Uri,
    content: Uint8Array,
    options: { create: boolean; overwrite: boolean },
  ): Promise<void> {
    const target = GxPartMapper.getObjectTarget(uri.path);
    const partName = this.getPart(uri);
    this._cache.contentCache.set(uri.toString(), content);
    this._cache.mtimes.set(uri.toString(), Date.now());

    try {
      const result = await this.callMcpTool(
        "genexus_edit",
        {
          name: target,
          part: partName,
          mode: "full",
          content: Buffer.from(content).toString("utf8"),
        },
        30000,
      );
      if (!result || result.error || result.status === "Error") {
        if (result?.issues && this._diagnosticProvider) {
          const editor = vscode.window.visibleTextEditors.find(
            (e: vscode.TextEditor) =>
              e.document.uri.toString() === uri.toString(),
          );
          if (editor)
            this._diagnosticProvider.setDiagnostics(
              editor.document,
              result.issues,
            );
        }
        throw new Error(result?.error || "Save failed");
      }

      this._cache.commitWrite(uri, partName);
      this._shadowService?.syncToDisk(uri, content, partName);
      this._emitter.fire([{ type: vscode.FileChangeType.Changed, uri }]);

      // Force directory refresh for structural changes
      this.clearDirCache();

      vscode.window.setStatusBarMessage(`$(check) Saved ${target}`, DEFAULT_STATUS_BAR_TIMEOUT);
    } catch (err: any) {
      vscode.window.showErrorMessage(`Save Error: ${err}`);
      throw err;
    }
  }

  public async triggerSave(
    uri: vscode.Uri,
    content: Uint8Array,
  ): Promise<void> {
    return this._writeFile(uri, content, { create: false, overwrite: true });
  }

  public async listMcpTools(customTimeout?: number): Promise<any[]> {
    return this._gateway.listMcpTools(customTimeout);
  }

  public async listMcpResources(customTimeout?: number): Promise<any[]> {
    return this._gateway.listMcpResources(customTimeout);
  }

  public async listMcpResourceTemplates(customTimeout?: number): Promise<any[]> {
    return this._gateway.listMcpResourceTemplates(customTimeout);
  }

  public async listMcpPrompts(customTimeout?: number): Promise<any[]> {
    return this._gateway.listMcpPrompts(customTimeout);
  }

  public async callMcpTool(
    name: string,
    args?: any,
    customTimeout?: number,
  ): Promise<any> {
    return this._gateway.callMcpTool(name, args, customTimeout);
  }

  public async readMcpResource(
    uri: string,
    customTimeout?: number,
  ): Promise<any> {
    return this._gateway.readMcpResource(uri, customTimeout);
  }

  public async getMcpPrompt(
    name: string,
    args?: any,
    customTimeout?: number,
  ): Promise<any> {
    return this._gateway.getMcpPrompt(name, args, customTimeout);
  }

  public async callMcpMethod(
    method: string,
    params?: any,
    customTimeout?: number,
  ): Promise<any> {
    return this._gateway.callMcp(method, params, customTimeout);
  }

  public async queryObjects(
    query: string,
    limit?: number,
    customTimeout?: number,
    filters?: { typeFilter?: string; domainFilter?: string },
  ): Promise<any> {
    const requestKey = `${query}::${limit ?? ""}::${customTimeout ?? ""}::${filters?.typeFilter ?? ""}::${filters?.domainFilter ?? ""}`;
    const pending = this._pendingQueryRequests.get(requestKey);
    if (pending) {
      return pending;
    }

    const request = this.callMcpTool(
      "genexus_query",
      {
        query,
        limit,
        ...(filters?.typeFilter ? { typeFilter: filters.typeFilter } : {}),
        ...(filters?.domainFilter ? { domainFilter: filters.domainFilter } : {}),
      },
      customTimeout,
    ).finally(() => {
      this._pendingQueryRequests.delete(requestKey);
    });

    this._pendingQueryRequests.set(requestKey, request);
    return request;
  }

  public async browseObjects(parentName: string): Promise<any[]> {
    const result = await this.queryObjects(
      `parent:"${parentName}" @quick`,
      BROWSE_QUERY_LIMIT,
      30000,
    );

    const objects = Array.isArray(result?.results)
      ? result.results
      : Array.isArray(result)
        ? result
        : [];

    if (!(result?.isTruncated === true)) {
      return objects;
    }

    const typedResults = await Promise.all(
      BROWSE_FALLBACK_TYPES.map(async (typeFilter) => {
        const typedResult = await this.queryObjects(
          `parent:"${parentName}" @quick`,
          BROWSE_QUERY_LIMIT,
          30000,
          { typeFilter },
        );

        return Array.isArray(typedResult?.results)
          ? typedResult.results
          : Array.isArray(typedResult)
            ? typedResult
            : [];
      }),
    );

    const unique = new Map<string, any>();
    for (const entry of typedResults.flat()) {
      if (!entry?.name || !entry?.type) continue;
      unique.set(`${entry.type}:${entry.name}`, entry);
    }

    return Array.from(unique.values());
  }

  public async readObjectVariables(name: string, customTimeout?: number): Promise<any[]> {
    const result = await this.readMcpResource(
      `genexus://objects/${name}/variables`,
      customTimeout,
    );
    return Array.isArray(result) ? result : [];
  }

  public async readAttributeMetadata(name: string, customTimeout?: number): Promise<any> {
    return this.readMcpResource(`genexus://attributes/${name}`, customTimeout);
  }

  public async inspectObject(
    name: string,
    include?: string[],
    customTimeout?: number,
  ): Promise<any> {
    return this.callMcpTool(
      "genexus_inspect",
      include && include.length > 0 ? { name, include } : { name },
      customTimeout,
    );
  }

  public async analyzeObject(
    name: string,
    mode: string,
    customTimeout?: number,
  ): Promise<any> {
    return this.callMcpTool("genexus_analyze", { name, mode }, customTimeout);
  }

  public async getStructure(
    name: string,
    action: "get_visual" | "update_visual" | "get_indexes" | "get_logic",
    payload?: any,
    customTimeout?: number,
  ): Promise<any> {
    return this.callMcpTool(
      "genexus_structure",
      payload === undefined ? { action, name } : { action, name, payload },
      customTimeout,
    );
  }

  public async formatCode(code: string, customTimeout?: number): Promise<any> {
    return this.callMcpTool("genexus_format", { code }, customTimeout);
  }

  public async refactor(
    args: {
      action: string;
      target?: string;
      newName?: string;
      objectName?: string;
      code?: string;
      procedureName?: string;
    },
    customTimeout?: number,
  ): Promise<any> {
    return this.callMcpTool("genexus_refactor", args, customTimeout);
  }

  public clearDirCache(): void {
    this._cache.clearDirectoryCache();
    this._emitter.fire([
      {
        type: vscode.FileChangeType.Changed,
        uri: vscode.Uri.from({ scheme: GX_SCHEME, path: "/" }),
      },
    ]);
  }

  createDirectory(uri: vscode.Uri): void {
    throw vscode.FileSystemError.NoPermissions("Not supported");
  }
  delete(uri: vscode.Uri, options: { recursive: boolean }): void {
    throw vscode.FileSystemError.NoPermissions("Not supported");
  }
  rename(
    oldUri: vscode.Uri,
    newUri: vscode.Uri,
    options: { overwrite: boolean },
  ): void {
    throw vscode.FileSystemError.NoPermissions("Not supported");
  }
  copy(
    source: vscode.Uri,
    destination: vscode.Uri,
    options: { overwrite: boolean },
  ): void {
    throw vscode.FileSystemError.NoPermissions("Not supported");
  }
}
