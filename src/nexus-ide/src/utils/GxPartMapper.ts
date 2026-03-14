import * as vscode from "vscode";

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

export const VALID_TYPES = new Set([
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
  "Domain",
  "Image",
]);

export class GxPartMapper {
  static getPart(uri: vscode.Uri, filePartState: Map<string, string>): string {
    const part = filePartState.get(uri.path);
    if (part) return part;

    if (uri.path.includes("/Table/")) return "Structure";
    return "Source";
  }

  static getCleanObjName(pathPart: string): string {
    const nameWithoutGx = pathPart.replace(/\.gx$/, "");
    const dotParts = nameWithoutGx.split(".");
    if (dotParts.length > 1) {
      const lastPart = dotParts[dotParts.length - 1];
      if (Object.values(TYPE_SUFFIX).includes(lastPart)) {
        return dotParts.slice(0, -1).join(".");
      }
    }
    return nameWithoutGx;
  }

  static getObjectTarget(uriPath: string): string | null {
    const cleanPath = uriPath.replace(/^\//, "");
    if (cleanPath.startsWith(".") || cleanPath.includes("/.")) {
      return null;
    }

    const parts = cleanPath.split("/");
    const typeStr = parts.length > 1 ? parts[0] : null;
    const fileName = parts[parts.length - 1];
    const objName = this.getCleanObjName(fileName);

    return typeStr && VALID_TYPES.has(typeStr)
      ? `${typeStr}:${objName}`
      : objName;
  }
}
