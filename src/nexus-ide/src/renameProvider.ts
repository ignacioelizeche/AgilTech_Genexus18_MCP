import * as vscode from 'vscode';

export class GxRenameProvider implements vscode.RenameProvider {
    constructor(private readonly callGateway: (cmd: any) => Promise<any>) {}

    async provideRenameEdits(
        document: vscode.TextDocument,
        position: vscode.Position,
        newName: string,
        _token: vscode.CancellationToken
    ): Promise<vscode.WorkspaceEdit | undefined> {
        const range = document.getWordRangeAtPosition(position);
        if (!range) return undefined;

        const oldName = document.getText(range);
        const objName = this.getObjName(document);
        const isVariable = oldName.startsWith('&');
        
        try {
            await vscode.window.withProgress({
                location: vscode.ProgressLocation.Notification,
                title: `Renaming ${isVariable ? 'Variable' : 'Attribute'} ${oldName}...`,
                cancellable: false
            }, async () => {
                const result = await this.callGateway({
                    method: 'execute_command',
                    params: {
                        module: 'Refactor',
                        action: isVariable ? 'RenameVariable' : 'RenameAttribute',
                        target: objName,
                        payload: JSON.stringify({
                            oldName: oldName,
                            newName: newName
                        })
                    }
                });

                if (result && result.error) {
                    throw new Error(result.error);
                }

                if (result && result.message) {
                    vscode.window.showInformationMessage(result.message);
                }
            });

            // Since the worker modifies the actual GeneXus object, 
            // the safest way to show changes in the IDE is to tell the user that the object was saved.
            // However, VS Code expects a WorkspaceEdit to be returned to update the current editor live.
            // Since we don't have a full multi-part editor sync yet, we will notify and refresh.
            
            if (!isVariable) {
                const reorg = await vscode.window.showWarningMessage(
                    `Attribute renamed. Would you like to check for database impact (Run Reorg)?`, 
                    'Yes', 'No'
                );
                if (reorg === 'Yes') {
                    vscode.commands.executeCommand('nexus-ide.runReorg');
                }
            } else {
                vscode.window.showInformationMessage(`Variable renamed successfully. Please reload all parts to see changes.`);
            }
            
            // Trigger a refresh of diagnostics and references
            vscode.commands.executeCommand('nexus-ide.refreshDiagnostics');
            
            // return empty edit to avoid VS Code trying to do a local simple text replace which might be out of sync
            return new vscode.WorkspaceEdit(); 
        } catch (e: any) {
            vscode.window.showErrorMessage(`Rename failed: ${e.message}`);
            return undefined;
        }
    }

    private getObjName(document: vscode.TextDocument): string {
        const path = decodeURIComponent(document.uri.path.substring(1));
        return path.split('/').pop()!.replace('.gx', '');
    }
}
