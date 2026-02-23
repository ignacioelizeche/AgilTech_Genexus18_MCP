import * as vscode from 'vscode';

export class GxDiagnosticProvider {
    private diagnosticCollection: vscode.DiagnosticCollection;

    constructor(private readonly callGateway: (cmd: any) => Promise<any>) {
        this.diagnosticCollection = vscode.languages.createDiagnosticCollection('genexus');
    }

    public async refreshDiagnostics(document: vscode.TextDocument): Promise<void> {
        if (document.languageId !== 'genexus') return;

        try {
            const objName = this.getObjName(document);
            const result = await this.callGateway({
                method: 'execute_command',
                params: { module: 'Linter', target: objName }
            });

            if (result && result.issues) {
                const diagnostics: vscode.Diagnostic[] = [];
                for (const issue of result.issues) {
                    const line = Math.max(0, (issue.line || 1) - 1);
                    const range = new vscode.Range(line, 0, line, 100); // Default to whole line if range unknown
                    
                    let severity = vscode.DiagnosticSeverity.Information;
                    if (issue.severity === 'Critical' || issue.severity === 'Error') severity = vscode.DiagnosticSeverity.Error;
                    else if (issue.severity === 'Warning') severity = vscode.DiagnosticSeverity.Warning;

                    const diagnostic = new vscode.Diagnostic(range, issue.description, severity);
                    diagnostic.code = issue.code;
                    diagnostic.source = 'GeneXus LSP';
                    diagnostics.push(diagnostic);
                }
                this.diagnosticCollection.set(document.uri, diagnostics);
            } else {
                this.diagnosticCollection.delete(document.uri);
            }
        } catch (e) {
            console.error("[Nexus IDE] Diagnostic error:", e);
        }
    }

    public async refreshAll(): Promise<void> {
        for (const editor of vscode.window.visibleTextEditors) {
            if (editor.document.languageId === 'genexus') {
                await this.refreshDiagnostics(editor.document);
            }
        }
    }

    public clear(document: vscode.TextDocument): void {
        this.diagnosticCollection.delete(document.uri);
    }

    private getObjName(document: vscode.TextDocument): string {
        const path = decodeURIComponent(document.uri.path.substring(1));
        return path.split('/').pop()!.replace('.gx', '');
    }

    public subscribeToEvents(context: vscode.ExtensionContext): void {
        context.subscriptions.push(vscode.workspace.onDidOpenTextDocument(doc => this.refreshDiagnostics(doc)));
        context.subscriptions.push(vscode.workspace.onDidSaveTextDocument(doc => this.refreshDiagnostics(doc)));
        
        // Debounced refresh for on-type diagnostics (Phase 1.1)
        let timeout: NodeJS.Timeout | undefined;
        context.subscriptions.push(vscode.workspace.onDidChangeTextDocument(e => {
            if (timeout) clearTimeout(timeout);
            timeout = setTimeout(() => this.refreshDiagnostics(e.document), 1500);
        }));

        context.subscriptions.push(vscode.workspace.onDidCloseTextDocument(doc => this.clear(doc)));
    }
}
