import * as vscode from 'vscode';

export class GxCodeLensProvider implements vscode.CodeLensProvider {
    private refCache = new Map<string, { count: number, time: number }>();

    constructor(private readonly callGateway: (cmd: any) => Promise<any>) {}

    async provideCodeLenses(
        document: vscode.TextDocument,
        _token: vscode.CancellationToken
    ): Promise<vscode.CodeLens[]> {
        const lenses: vscode.CodeLens[] = [];
        const path = decodeURIComponent(document.uri.path.substring(1));
        const objName = path.split('/').pop()!.replace('.gx', '');

        // Add CodeLens at the first line of the document
        const range = new vscode.Range(0, 0, 0, 0);
        
        // We defer the actual count fetching to resolveCodeLens for performance
        const lens = new vscode.CodeLens(range);
        lenses.push(lens);

        return lenses;
    }

    async resolveCodeLens(
        codeLens: vscode.CodeLens,
        _token: vscode.CancellationToken
    ): Promise<vscode.CodeLens> {
        const activeEditor = vscode.window.activeTextEditor;
        if (!activeEditor) return codeLens;

        const path = decodeURIComponent(activeEditor.document.uri.path.substring(1));
        const objName = path.split('/').pop()!.replace('.gx', '');

        // Use cache (5 minute ttl) to avoid hammering during scrolling/typing
        const cached = this.refCache.get(objName);
        if (cached && (Date.now() - cached.time < 300000)) {
            codeLens.command = {
                title: `${cached.count} references`,
                command: 'gx.showReferences',
                arguments: [objName]
            };
            return codeLens;
        }

        try {
            const results = await this.callGateway({
                method: 'execute_command',
                params: { module: 'Search', query: `usedby:${objName}`, limit: 1 }
            });

            const count = (results && results.count !== undefined) ? results.count : (results?.results?.length || 0);
            this.refCache.set(objName, { count, time: Date.now() });

            codeLens.command = {
                title: `${count} references`,
                command: 'gx.showReferences',
                arguments: [objName]
            };
        } catch (e) {
            codeLens.command = {
                title: "0 references",
                command: ""
            };
        }

        return codeLens;
    }
}
