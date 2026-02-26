import * as vscode from "vscode";
import * as http from "http";
import * as fs from "fs";
import * as path from "path";
import { GxShadowService } from "./gxShadowService";
import { GxDiagnosticProvider } from "./diagnosticProvider";

// Maps GeneXus type names → suffix before .gx
export const TYPE_SUFFIX: Record<string, string> = {
  Procedure: "prc",
  WebPanel: "wp",
  Transaction: "trn",
  SDT: "sdt",
  StructuredDataType: "sdt",
  DataProvider: "dp",
  DataView: "dv",
  Attribute: "att",
  Table: "tab",
  SDPanel: "sdp",
};

export class GxFileSystemProvider implements vscode.FileSystemProvider {
  private _emitter = new vscode.EventEmitter<vscode.FileChangeEvent[]>();
  readonly onDidChangeFile: vscode.Event<vscode.FileChangeEvent[]> =
    this._emitter.event;

  private _baseUrl = "http://localhost:5000/api/command";
  public set baseUrl(value: string) {
    this._baseUrl = value;
  }
  public get baseUrl(): string {
    return this._baseUrl;
  }

  private _shadowService?: GxShadowService;
  public setShadowService(service: GxShadowService) {
    this._shadowService = service;
  }

  private _diagnosticProvider?: GxDiagnosticProvider;
  public setDiagnosticProvider(provider: GxDiagnosticProvider) {
    this._diagnosticProvider = provider;
  }

  private _filePartState = new Map<string, string>(); // Maps uri.path -> partName
  private _contentCache = new Map<string, Uint8Array>(); // Holds content AFTER a save or latest read from Worker
  private _mtimes = new Map<string, number>(); // Stores mtime per URI
  private _readCache = new Map<string, { data: Uint8Array; time: number }>(); // Read cache from Worker for performance
  private _pendingReadRequests = new Map<string, Promise<Uint8Array>>();
  private _dirCache = new Map<
    string,
    { entries: [string, vscode.FileType][]; time: number }
  >();
  private _partsCache = new Map<string, Map<string, Uint8Array>>(); // uri.toString() -> partName -> content
  private _metadataCache = new Map<string, string>(); // Persistent Shadowing for Trn/SDT structures
  private readonly VALID_TYPES = new Set([
    "Procedure",
    "Transaction",
    "WebPanel",
    "DataProvider",
    "Attribute",
    "Table",
    "DataView",
    "SDPanel",
    "SDT",
    "StructuredDataType",
  ]);

  public setPart(uri: vscode.Uri, partName: string) {
    console.log(`[Nexus IDE] setPart: Setting ${uri.path} to ${partName}`);
    this._filePartState.set(uri.path, partName);
    this._readCache.delete(this.getCacheKey(uri, partName)); // Invalidate specific part cache
    this._contentCache.delete(uri.toString()); // Invalidate shadowing cache for this URI

    // Update mtime to force VS Code to reload the document
    this._mtimes.set(uri.toString(), Date.now());

    this._emitter.fire([{ type: vscode.FileChangeType.Changed, uri }]); // Notify VS Code of change
  }

  public getPart(uri: vscode.Uri): string {
    const part = this._filePartState.get(uri.path);
    if (part) return part;

    // Default part for Tables is Structure, others are Source
    if (uri.path.includes("/Table/")) return "Structure";
    return "Source";
  }

  private getCacheKey(uri: vscode.Uri, partName: string): string {
    return uri.toString() + "?" + partName;
  }

  private getCleanObjName(pathPart: string): string {
    // Removes .prc.gx, .trn.gx, etc. to get only the object name
    const nameWithoutGx = pathPart.replace(/\.gx$/, "");
    const dotParts = nameWithoutGx.split(".");
    if (dotParts.length > 1) {
      // Check if last part is a known suffix
      const lastPart = dotParts[dotParts.length - 1];
      if (Object.values(TYPE_SUFFIX).includes(lastPart)) {
        return dotParts.slice(0, -1).join(".");
      }
    }
    return nameWithoutGx;
  }

  public async initKb() {
    console.log("[Nexus IDE] Warming up KB...");

    // Fast Path: Check if Gateway is already ready
    try {
      const health = await this.callGateway(
        {
          method: "execute_command",
          params: { module: "Health", action: "Ping" },
        },
        2000,
      );
      if (health) console.log("[Nexus IDE] Backend already warm.");
    } catch {}

    const initPromise = this.callGateway({
      method: "execute_command",
      params: { module: "KB", action: "Initialize" },
    });

    // PERFORMANCE: Pre-fetch Root immediately without waiting for SDK init (rely on Search Cache)
    this.readDirectory(vscode.Uri.parse("genexus:/")).catch(() => {});

    initPromise.then(() => {
      console.log(
        "[Nexus IDE] KB SDK Init complete. Triggering background pre-fetch...",
      );
      const commonParents = ["General", "Common", "API"];
      commonParents.forEach((parent) => {
        this.readDirectory(vscode.Uri.parse(`genexus:/${parent}`)).catch(
          () => {},
        );
      });

      // PERFORMANCE: Background Metadata Shadowing for structured objects
      setTimeout(() => this.shadowMetadata(), 1000); // 1s wait is enough now
    });

    return initPromise;
  }

  private async shadowMetadata() {
    try {
      console.log(
        "[Nexus IDE] Background Shadowing: Fetching important structures...",
      );
      // Get top 20 transactions/sdts for shadowing
      const result = await this.callGateway({
        method: "execute_command",
        params: {
          module: "Search",
          query: "type:Transaction or type:SDT",
          limit: 20,
        },
      });

      if (result && result.results) {
        for (const obj of result.results) {
          const suffix = TYPE_SUFFIX[obj.type]
            ? `.${TYPE_SUFFIX[obj.type]}`
            : "";
          const uri = vscode.Uri.parse(
            `genexus:/${obj.type}/${obj.name}${suffix}.gx`,
          );
          const part = obj.type === "Transaction" ? "Structure" : "Source";
          this.fetchAndCacheMetadata(uri, obj.type, obj.name, part);
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
      const target = this.VALID_TYPES.has(objType)
        ? `${objType}:${objName}`
        : objName;
      const res = await this.callGateway({
        method: "execute_command",
        params: {
          module: "Read",
          action: "ExtractSource",
          target: target,
          part: partName,
        },
      });
      if (res && res.source) {
        let decoded = res.isBase64
          ? Buffer.from(res.source, "base64").toString("utf8")
          : res.source;
        this._metadataCache.set(uri.toString() + ":" + partName, decoded);
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
    const pathStr = decodeURIComponent(uri.path.substring(1));

    if (
      pathStr.startsWith(".") ||
      pathStr.includes("tasks.json") ||
      pathStr.includes("settings.json") ||
      pathStr.includes("launch.json") ||
      pathStr.includes("pom.xml")
    ) {
      throw vscode.FileSystemError.FileNotFound(uri);
    }

    if (pathStr === "" || !pathStr.endsWith(".gx")) {
      return {
        type: vscode.FileType.Directory,
        ctime: Date.now(),
        mtime: Date.now(),
        size: 0,
      };
    }

    const cachedContent = this._contentCache.get(uri.toString());

    // PERFORMANCE: If not in memory cache, don't guess 1024.
    // We could look into Shadow Mirror or Search Cache for actual size.
    let size = cachedContent ? cachedContent.byteLength : 0;

    if (size === 0) {
      // Check if it's a physical mirror file to get accurate size without calling SDK
      const objName = pathStr.split("/").pop()!.replace(".gx", "");
      const objType = pathStr.split("/")[0];
      const shadowPath = path.join(
        this._shadowService?.shadowRoot || "",
        objType,
        `${objName}.gx`,
      );
      if (fs.existsSync(shadowPath)) {
        size = fs.statSync(shadowPath).size;
      } else {
        size = 0; // Defaulting to 0 for virtual files is often safer than 1024 for VS Code's internal diffing
      }
    }

    if (!this._mtimes.has(uri.toString())) {
      this._mtimes.set(uri.toString(), Date.now());
    }

    return {
      type: vscode.FileType.File,
      ctime: Date.now(),
      mtime: this._mtimes.get(uri.toString())!,
      size: size,
    };
  }

  async readDirectory(uri: vscode.Uri): Promise<[string, vscode.FileType][]> {
    const pathStr = decodeURIComponent(uri.path.substring(1));
    const parentName =
      pathStr === "" ? "Root Module" : pathStr.split("/").pop()!;
    const cacheKey = `dir:${pathStr}`;

    const cached = this._dirCache.get(cacheKey);
    if (cached && Date.now() - cached.time < 300000) return cached.entries;

    try {
      const isTypeFolder = this.VALID_TYPES.has(parentName);
      const query = isTypeFolder
        ? `type:${parentName}`
        : `parent:"${parentName}"`;

      const result = await this.callGateway({
        method: "execute_command",
        params: { module: "Search", query: query, limit: 5000 },
      });

      const objects = result.results || (Array.isArray(result) ? result : []);
      if (Array.isArray(objects)) {
        const mapped = objects.map((obj: any) => {
          const isDir = obj.type === "Folder" || obj.type === "Module";
          let name = obj.name;
          if (!isDir) {
            const suffix = TYPE_SUFFIX[obj.type]
              ? `.${TYPE_SUFFIX[obj.type]}`
              : "";
            name = `${obj.name}${suffix}.gx`;
          }
          return [
            name,
            isDir ? vscode.FileType.Directory : vscode.FileType.File,
          ];
        }) as [string, vscode.FileType][];

        this._dirCache.set(cacheKey, { entries: mapped, time: Date.now() });
        return mapped;
      }
    } catch (e) {
      console.error(`[Nexus IDE] readDirectory error for ${parentName}:`, e);
    }
    return [];
  }

  async readFile(uri: vscode.Uri): Promise<Uint8Array> {
    const pathStr = decodeURIComponent(uri.path.substring(1));
    const partName = this.getPart(uri);
    const uriStr = uri.toString();
    const cacheKey = this.getCacheKey(uri, partName);

    // 1. Check current part in parts cache (Instant swap!)
    const pCache = this._partsCache.get(uriStr);
    if (pCache && pCache.has(partName)) {
      return pCache.get(partName)!;
    }

    const shadowed = this._metadataCache.get(uriStr + ":" + partName);
    if (shadowed) return Buffer.from(shadowed, "utf8");

    if (this._contentCache.has(uriStr)) {
      return this._contentCache.get(uriStr)!;
    }

    const cached = this._readCache.get(cacheKey);
    if (cached && Date.now() - cached.time < 30000) {
      return cached.data;
    }

    if (this._pendingReadRequests.has(cacheKey)) {
      return this._pendingReadRequests.get(cacheKey)!;
    }

    const request = (async () => {
      const parts = pathStr.split("/");
      const typeStr = parts.length > 1 ? parts[0] : null;
      const fileName = parts[parts.length - 1];
      const objName = this.getCleanObjName(fileName);
      const target =
        typeStr && this.VALID_TYPES.has(typeStr)
          ? `${typeStr}:${objName}`
          : objName;

      try {
        // PERFORMANCE: Fetch all parts in one go to make switching instant
        const allPartsResult = await this.callGateway({
          method: "execute_command",
          params: {
            module: "Read",
            action: "ExtractAllParts",
            target: target,
          },
        });

        if (allPartsResult && allPartsResult.parts) {
          const newPCache = new Map<string, Uint8Array>();
          for (const [p, content64] of Object.entries(allPartsResult.parts)) {
            const data = Buffer.from(content64 as string, "base64");
            newPCache.set(p, data);
          }
          this._partsCache.set(uriStr, newPCache);

          if (newPCache.has(partName)) {
            const data = newPCache.get(partName)!;
            this._contentCache.set(uriStr, data);
            this._shadowService?.syncToDisk(uri, data, partName);
            return data;
          }
        }

        // Fallback to single part read if multi-part failed or returned nothing
        const result = await this.callGateway({
          method: "execute_command",
          params: {
            module: "Read",
            action: "ExtractSource",
            target: target,
            part: partName,
          },
        });
        let data: Uint8Array;
        if (result && result.source) {
          if (result.isBase64) {
            data = Buffer.from(result.source, "base64");
          } else {
            data = Buffer.from(result.source, "utf8");
          }
        } else if (result && result.error) {
          data = Buffer.from(
            `// Error from Gateway: ${result.error}\n// Target: ${objName}, Part: ${partName}`,
            "utf8",
          );
        } else {
          data = Buffer.from(
            `// Part not available: ${partName}\n// Object: ${objName}`,
            "utf8",
          );
        }

        this._readCache.set(cacheKey, { data, time: Date.now() });
        this._contentCache.set(uriStr, data);
        this._shadowService?.syncToDisk(uri, data, partName);

        return data;
      } catch (error) {
        return Buffer.from(`// Error reading part: ${error}`, "utf8");
      } finally {
        this._pendingReadRequests.delete(cacheKey);
      }
    })();
    this._pendingReadRequests.set(cacheKey, request);
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
    const uriStr = uri.toString();
    if (this._partsCache.has(uriStr) || this._pendingReadRequests.has(uriStr)) {
      return;
    }

    console.log(`[Nexus IDE] Pre-warming: ${uri.path}`);
    // Trigger readFile but don't await it here. This will populate the caches.
    this.readFile(uri).catch(() => {});
  }

  async _writeFile(
    uri: vscode.Uri,
    content: Uint8Array,
    options: { create: boolean; overwrite: boolean },
  ): Promise<void> {
    console.log(`[Nexus IDE] 🔥 _writeFile START: ${uri.path}`);
    const pathStr = decodeURIComponent(uri.path.substring(1));
    const parts = pathStr.split("/");
    const typeStr = parts.length > 1 ? parts[0] : null;
    const fileName = parts[parts.length - 1];
    const objName = this.getCleanObjName(fileName);
    const target =
      typeStr && this.VALID_TYPES.has(typeStr)
        ? `${typeStr}:${objName}`
        : objName;
    const partName = this.getPart(uri);
    const source = Buffer.from(content).toString("utf8");
    const base64Source = Buffer.from(content).toString("base64");

    console.log(
      `[Nexus IDE] 💾 Saving (Base64): ${target} (Part: ${partName}) - Original Length: ${source.length}`,
    );

    this._contentCache.set(uri.toString(), content);
    this._mtimes.set(uri.toString(), Date.now());

    try {
      const result = await this.callGateway({
        method: "execute_command",
        params: {
          module: "Write",
          target: target,
          action: partName,
          payload: base64Source,
        },
      });

      const isError =
        !result ||
        (typeof result === "object" &&
          (result.error || result.status === "Error"));
      const isEmpty =
        !result || (typeof result === "string" && result.trim() === "");

      if (isError || isEmpty) {
        if (result && result.issues && this._diagnosticProvider) {
          const editor = vscode.window.visibleTextEditors.find(
            (e) => e.document.uri.toString() === uri.toString(),
          );
          if (editor) {
            this._diagnosticProvider.setDiagnostics(
              editor.document,
              result.issues,
            );
          }
        }

        const errorDetail =
          result?.error ||
          result?.output ||
          (isEmpty ? "Empty response from Worker" : "Unknown Worker Error");
        throw new Error(errorDetail);
      }

      const cacheKey = this.getCacheKey(uri, partName);
      this._readCache.delete(cacheKey);
      this._partsCache.delete(uri.toString()); // Invalidate all parts cache on write

      // Shadow Sync (Background) - Keep disk in sync with KB
      this._shadowService?.syncToDisk(uri, content, partName);

      this._emitter.fire([{ type: vscode.FileChangeType.Changed, uri }]);
      vscode.window.setStatusBarMessage(`$(check) Saved ${objName}`, 5000);
    } catch (err) {
      console.error(`[Nexus IDE] ❌ Save Error: ${err}`);
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

  public async callGateway(command: any, customTimeout?: number): Promise<any> {
    return new Promise((resolve, reject) => {
      // INJECT SHADOW PATH FOR WORKER TRANSPARENCY
      if (this._shadowService && command.params) {
        command.params.shadowPath = this._shadowService.shadowRoot;
      }

      const data = JSON.stringify(command);
      const timeout = customTimeout || 60000;
      const req = http.request(
        this._baseUrl,
        {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            "Content-Length": Buffer.byteLength(data),
          },
          timeout: timeout,
        },
        (res) => {
          let body = "";
          res.on("data", (chunk) => (body += chunk));
          res.on("end", () => {
            try {
              resolve(JSON.parse(body));
            } catch {
              resolve(body);
            }
          });
        },
      );
      req.on("timeout", () => {
        req.destroy();
        reject(new Error(`Timeout Gateway (${timeout / 1000}s)`));
      });
      req.on("error", reject);
      req.write(data);
      req.end();
    });
  }

  public clearDirCache(): void {
    this._dirCache.clear();
    this._emitter.fire([
      {
        type: vscode.FileChangeType.Changed,
        uri: vscode.Uri.parse("genexus:/"),
      },
    ]);
  }

  createDirectory(): void {
    throw vscode.FileSystemError.NoPermissions(
      "Nexus IDE: Creation of folders is not supported yet.",
    );
  }
  delete(): void {
    throw vscode.FileSystemError.NoPermissions(
      "Nexus IDE: Deletion is not supported yet.",
    );
  }
  rename(): void {
    throw vscode.FileSystemError.NoPermissions(
      "Nexus IDE: Rename is not supported yet.",
    );
  }
}
