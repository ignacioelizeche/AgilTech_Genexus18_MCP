import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import * as crypto from 'crypto';
import { TYPE_SUFFIX } from './gxFileSystem';
import { GxUriParser } from './utils/GxUriParser';
import { GxGatewayClient } from './infra/GxGatewayClient';

export class GxShadowService {
    private _shadowRoot: string;
    private _baseUrl: string;
    private _gateway: GxGatewayClient;
    private _fileHashes: Map<string, string> = new Map();
    private _fileContentCache: Map<string, string> = new Map();
    private readonly MAX_HASHES = 500;

    constructor(baseUrl: string) {
        this._baseUrl = baseUrl;
        this._gateway = new GxGatewayClient(baseUrl);
        
        let workspaceRoot = vscode.workspace.workspaceFolders?.find(f => f.uri.scheme === 'file')?.uri.fsPath;
        
        // Seletion Fallback
        if (!workspaceRoot || workspaceRoot.startsWith('genexus')) {
            let current = __dirname;
            while (current !== path.dirname(current)) {
                if (fs.existsSync(path.join(current, '.git')) || fs.existsSync(path.join(current, 'Genexus18MCP.sln'))) {
                    workspaceRoot = current;
                    break;
                }
                current = path.dirname(current);
            }
            if (!workspaceRoot) workspaceRoot = process.cwd();
        }
            
        this._shadowRoot = path.join(workspaceRoot, '.gx_mirror');
        
        if (!fs.existsSync(this._shadowRoot)) {
            fs.mkdirSync(this._shadowRoot, { recursive: true });
        }
    }

    /**
     * Sincroniza um objeto da KB para o disco (.gx_shadow/)
     */
    public async syncToDisk(uri: vscode.Uri, content: Uint8Array, part: string): Promise<string | null> {
        try {
            if (!this._shadowRoot) return null;

            const info = GxUriParser.parse(uri);
            if (!info) return null;

            const { type, name: objName } = info;
            const typeDir = path.join(this._shadowRoot, type);
            if (!fs.existsSync(typeDir)) fs.mkdirSync(typeDir, { recursive: true });

            const cleanPart = part === 'Source' ? '' : `.${part}`;
            const shadowFileName = `${objName}${cleanPart}.gx`;
            const shadowPath = path.join(typeDir, shadowFileName);

            const hash = crypto.createHash('sha256').update(content).digest('hex');
            const strContent = new TextDecoder().decode(content);
            
            if (this._fileHashes.size >= this.MAX_HASHES) {
                const firstKey = this._fileHashes.keys().next().value;
                if (firstKey) {
                    this._fileHashes.delete(firstKey);
                    this._fileContentCache.delete(firstKey);
                }
            }
            this._fileHashes.set(shadowPath, hash);
            this._fileContentCache.set(shadowPath, strContent);
            
            const tmpPath = `${shadowPath}.tmp`;
            fs.writeFileSync(tmpPath, content);
            fs.renameSync(tmpPath, shadowPath);

            console.log(`[Shadow Service] 🚀 Mirrored ${objName} (${part}) to disk.`);
            return shadowPath;
        } catch (e) {
            console.error(`[Shadow Service] ❌ SyncToDisk failed: ${e}`);
            return null;
        }
    }

    public shouldIgnore(filePath: string): boolean {
        if (!fs.existsSync(filePath)) return true;
        
        try {
            const content = fs.readFileSync(filePath);
            const currentHash = crypto.createHash('sha256').update(content).digest('hex');
            const expectedHash = this._fileHashes.get(filePath);
            
            if (currentHash === expectedHash) return true;
            
            this._fileHashes.set(filePath, currentHash);
            return false;
        } catch (e) {
            return false;
        }
    }

    private async tryDeltaSync(filePath: string, newContent: string, objName: string, partName: string): Promise<boolean> {
        const oldContent = this._fileContentCache.get(filePath);
        if (!oldContent) return false;

        const oldLines = oldContent.split(/\r?\n/);
        const newLines = newContent.split(/\r?\n/);

        // Simple single-block diff: find first and last differing lines
        let start = 0;
        while (start < oldLines.length && start < newLines.length && oldLines[start] === newLines[start]) {
            start++;
        }

        let oldEnd = oldLines.length - 1;
        let newEnd = newLines.length - 1;
        while (oldEnd >= start && newEnd >= start && oldLines[oldEnd] === newLines[newEnd]) {
            oldEnd--;
            newEnd--;
        }

        // If change is surgical (less than 10 lines changed in a large file), use Patch
        const linesChanged = (newEnd - start + 1);
        if (linesChanged > 0 && linesChanged <= 10) {
            const oldFragment = oldLines.slice(start, oldEnd + 1).join('\n');
            const newFragment = newLines.slice(start, newEnd + 1).join('\n');

            if (oldFragment.length > 0) {
                console.log(`[Shadow Service] ⚡ Delta Sync (Patch) for ${objName}.${partName}: ${linesChanged} lines.`);
                const response = await this._gateway.callMcpTool('genexus_edit', {
                    name: objName,
                    part: partName,
                    mode: 'patch',
                    operation: 'Replace',
                    context: oldFragment,
                    content: newFragment,
                }, 30000);
                return !response?.error && response?.status !== 'Error';
            }
        }
        return false;
    }

    /**
     * Sincroniza uma mudança no disco (.gx_shadow/) de volta para a KB
     */
    public async syncToKB(filePath: string): Promise<void> {
        try {
            const content = fs.readFileSync(filePath, 'utf8');
            const relativePath = path.relative(this._shadowRoot, filePath);
            const parts = relativePath.split(path.sep);

            if (parts.length < 2) return;

            const type = parts[0];
            const fileName = parts[1];
            const fileNoExt = fileName.replace(/\.gx$/, '');
            const dotParts = fileNoExt.split('.');
            const objName = dotParts[0];
            const partName = dotParts.length > 1 ? dotParts[1] : 'Source';

            // TRY DELTA SYNC (PATCH) FIRST
            const deltaSuccess = await this.tryDeltaSync(filePath, content, objName, partName);
            
            if (!deltaSuccess) {
                console.log(`[Shadow Service] 💾 Full Write: ${objName} (Part: ${partName})`);
                const response = await this._gateway.callMcpTool('genexus_edit', {
                    name: objName,
                    part: partName,
                    mode: 'full',
                    content,
                }, 30000);

                if (response?.error || response?.status === 'Error') {
                    throw new Error(response?.error || 'Gateway write failed');
                }
            }

            // Always update cache after sync
            this._fileContentCache.set(filePath, content);

        } catch (e) {
            console.error(`[Shadow Service] ❌ SyncToKB failed for ${filePath}: ${e}`);
            vscode.window.showErrorMessage(`Shadow Sync Error: ${e}`);
        }
    }

    public get shadowRoot(): string { return this._shadowRoot; }
}
