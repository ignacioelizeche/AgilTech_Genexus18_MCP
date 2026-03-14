import * as vscode from 'vscode';

export class GxCodeActionProvider implements vscode.CodeActionProvider {
    public static readonly kind = vscode.CodeActionKind.QuickFix;

    public async provideCodeActions(
        document: vscode.TextDocument,
        range: vscode.Range | vscode.Selection,
        _context: vscode.CodeActionContext,
        _token: vscode.CancellationToken
    ): Promise<vscode.CodeAction[]> {
        const actions: vscode.CodeAction[] = [];
        const wordRange = document.getWordRangeAtPosition(range.start);
        if (!wordRange) return [];

        const word = document.getText(wordRange);
        if (word.startsWith('&')) {
            const varName = word.substring(1);
            
            // In a real implementation, we would check if the variable exists first.
            // For now, we offer the fix if we're on a variable.
            const action = new vscode.CodeAction(`Create Variable &${varName}`, GxCodeActionProvider.kind);
            action.command = {
                command: 'nexus-ide.createVariable',
                title: 'Create Variable',
                arguments: [document.uri, varName]
            };
            actions.push(action);
        }

        return actions;
    }
}
