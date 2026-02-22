import * as vscode from 'vscode';

export class GxRenameProvider implements vscode.RenameProvider {
    async prepareRename(document: vscode.TextDocument, position: vscode.Position, _token: vscode.CancellationToken): Promise<vscode.Range | { range: vscode.Range; placeholder: string } | undefined> {
        const range = document.getWordRangeAtPosition(position);
        if (!range) return undefined;
        const word = document.getText(range);
        if (word.startsWith('&')) {
            return range;
        }
        throw new Error("Only local variables (&var) can be renamed in Nexus IDE currently.");
    }

    async provideRenameEdits(
        document: vscode.TextDocument,
        position: vscode.Position,
        newName: string,
        _token: vscode.CancellationToken
    ): Promise<vscode.WorkspaceEdit | undefined> {
        const range = document.getWordRangeAtPosition(position);
        if (!range) return undefined;
        const oldName = document.getText(range);

        // We only support local variable renaming within the same file for now.
        // Full global rename of attributes would require recursive usedby: search.
        const edit = new vscode.WorkspaceEdit();
        const text = document.getText();
        
        // Regex to find all occurrences of the variable
        const escapedOldName = oldName.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
        const regex = new RegExp(`\\b${escapedOldName}\\b`, 'g');
        
        let match;
        while ((match = regex.exec(text)) !== null) {
            const startPos = document.positionAt(match.index);
            const endPos = document.positionAt(match.index + oldName.length);
            edit.replace(document.uri, new vscode.Range(startPos, endPos), newName);
        }

        return edit;
    }
}
