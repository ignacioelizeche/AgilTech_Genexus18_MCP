import * as vscode from 'vscode';

export class GxDefinitionProvider implements vscode.DefinitionProvider {
    constructor(private readonly callGateway: (cmd: any) => Promise<any>) {}

    async provideDefinition(
        document: vscode.TextDocument,
        position: vscode.Position,
        _token: vscode.CancellationToken
    ): Promise<vscode.Definition | undefined> {
        const line = document.lineAt(position.line).text;
        const range = document.getWordRangeAtPosition(position);
        if (!range) return undefined;
        
        const word = document.getText(range);

        // 1. Check if it's a Subroutine call: do 'SubName' or do SubName
        const doMatch = line.match(/\bdo\s+['"]?([a-zA-Z0-9_]+)['"]?/i);
        if (doMatch && line.includes(word) && word === doMatch[1]) {
            const subName = doMatch[1];
            const text = document.getText();
            const subDefRegex = new RegExp(`\\b(sub)\\s+['"]?${subName}['"]?`, 'gi');
            let match;
            while ((match = subDefRegex.exec(text)) !== null) {
                const startPos = document.positionAt(match.index);
                return new vscode.Location(document.uri, startPos);
            }
        }

        // 2. KB Object Search (Remote)
        try {
            const result = await this.callGateway({
                method: 'execute_command',
                params: { module: 'Search', query: word, limit: 10 }
            });

            if (result && result.results && result.results.length > 0) {
                // Find exact match first
                const exactMatch = result.results.find((obj: any) => obj.name.toLowerCase() === word.toLowerCase());
                if (exactMatch) {
                    const uri = vscode.Uri.parse(`genexus:/${exactMatch.type}/${exactMatch.name}.gx`);
                    return new vscode.Location(uri, new vscode.Position(0, 0));
                }
            }
        } catch (e) {
            console.error("[Nexus IDE] Definition error:", e);
        }

        return undefined;
    }
}
