import * as vscode from 'vscode';

export class GxFormatProvider implements vscode.DocumentFormattingEditProvider {
    constructor(private readonly callGateway: (cmd: any) => Promise<any>) {}

    async provideDocumentFormattingEdits(
        document: vscode.TextDocument,
        _options: vscode.FormattingOptions,
        _token: vscode.CancellationToken
    ): Promise<vscode.TextEdit[]> {
        try {
            const content = document.getText();
            const result = await this.callGateway({
                method: 'execute_command',
                params: {
                    module: 'Formatting',
                    action: 'Format',
                    payload: content
                }
            });

            if (result && result.formatted) {
                const fullRange = new vscode.Range(
                    document.positionAt(0),
                    document.positionAt(content.length)
                );
                return [vscode.TextEdit.replace(fullRange, result.formatted)];
            }
        } catch (e) {
            console.error("[Nexus IDE] Formatting error:", e);
            vscode.window.showErrorMessage("Formatting failed: " + e);
        }

        return [];
    }
}
