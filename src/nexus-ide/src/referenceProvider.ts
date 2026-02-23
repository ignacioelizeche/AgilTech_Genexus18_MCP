import * as vscode from 'vscode';

export class GxReferenceProvider implements vscode.ReferenceProvider {
    constructor(private readonly callGateway: (cmd: any) => Promise<any>) {}

    async provideReferences(
        document: vscode.TextDocument,
        position: vscode.Position,
        context: vscode.ReferenceContext,
        _token: vscode.CancellationToken
    ): Promise<vscode.Location[]> {
        const range = document.getWordRangeAtPosition(position);
        if (!range) return [];
        const word = document.getText(range);

        // Remove & if it's a variable reference (we don't support global variable search yet, searching for object uses)
        const targetName = word.startsWith('&') ? word.substring(1) : word;

        try {
            const results = await this.callGateway({
                method: 'execute_command',
                params: { module: 'Search', query: `usedby:${targetName}`, limit: 100 }
            });

            if (results && results.results) {
                return results.results.map((obj: any) => {
                    const uri = vscode.Uri.parse(`genexus:/${obj.type}/${obj.name}.gx`);
                    return new vscode.Location(uri, new vscode.Position(0, 0));
                });
            }
        } catch (e) {
            console.error("[Nexus IDE] Reference Provider error:", e);
        }

        return [];
    }
}
