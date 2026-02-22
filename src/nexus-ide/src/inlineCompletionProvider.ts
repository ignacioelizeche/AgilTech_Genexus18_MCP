import * as vscode from 'vscode';

export class GxInlineCompletionItemProvider implements vscode.InlineCompletionItemProvider {
    async provideInlineCompletionItems(
        document: vscode.TextDocument,
        position: vscode.Position,
        _context: vscode.InlineCompletionContext,
        _token: vscode.CancellationToken
    ): Promise<vscode.InlineCompletionItem[] | vscode.InlineCompletionList> {
        const items: vscode.InlineCompletionItem[] = [];
        const lineText = document.lineAt(position).text;
        const lineUntilCursor = lineText.substring(0, position.character);

        // 1. Logic for patterns like &var.
        const dotMatch = lineUntilCursor.match(/&([a-zA-Z0-9_]+)\.$/);
        if (dotMatch) {
            const varName = dotMatch[1];
            // Basic generic suggestions as ghost text
            items.push(new vscode.InlineCompletionItem("IsEmpty()", new vscode.Range(position, position)));
            items.push(new vscode.InlineCompletionItem("SetEmpty()", new vscode.Range(position, position)));
            
            if (varName.toLowerCase().includes('coll') || varName.toLowerCase().endsWith('s')) {
                items.push(new vscode.InlineCompletionItem("Count", new vscode.Range(position, position)));
            }
        }

        // 2. Logic for control structures
        if (lineUntilCursor.trim().toLowerCase() === 'if') {
            items.push(new vscode.InlineCompletionItem(" &var.IsEmpty()", new vscode.Range(position, position)));
        }

        if (lineUntilCursor.trim().toLowerCase() === 'for each') {
            items.push(new vscode.InlineCompletionItem(" defined by ${1:Attribute}", new vscode.Range(position, position)));
        }

        return items;
    }
}
