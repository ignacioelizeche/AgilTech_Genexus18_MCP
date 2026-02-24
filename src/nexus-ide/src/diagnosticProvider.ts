import * as vscode from 'vscode';
import { GxFileSystemProvider } from './gxFileSystem';

export class GxDiagnosticProvider {
    private diagnosticCollection: vscode.DiagnosticCollection;

    constructor(
        private readonly callGateway: (cmd: any) => Promise<any>,
        private readonly fsProvider: GxFileSystemProvider
    ) {
        this.diagnosticCollection = vscode.languages.createDiagnosticCollection('genexus');
    }

    public async refreshDiagnostics(document: vscode.TextDocument): Promise<void> {
        if (document.languageId !== 'genexus') return;

        try {
            const objName = this.getObjName(document);
            const currentPart = this.getPartName(document.uri);

            const result = await this.callGateway({
                method: 'execute_command',
                params: { 
                    module: 'Linter', 
                    target: objName,
                    part: currentPart 
                }
            });

            if (result && result.issues) {
                const diagnostics: vscode.Diagnostic[] = [];

                for (const issue of result.issues) {
                    // FILTRO ELITE: 
                    // Se estivermos na aba Variables, só mostra erros de Variables.
                    // Se estivermos em abas de Código (Source/Events/Rules), mostra erros específicos da part OU erros genéricos de 'Logic'.
                    if (currentPart === 'Variables') {
                        if (issue.part !== 'Variables') continue;
                    } else {
                        if (issue.part === 'Variables') continue;
                        if (issue.part !== currentPart && issue.part !== 'Logic') {
                            // Fallback: se a part do erro for 'Source' e o editor for 'Procedure' (ou vice-versa), permite.
                            if (!(currentPart === 'Source' && issue.part === 'Procedure') && 
                                !(currentPart === 'Procedure' && issue.part === 'Source')) {
                                continue;
                            }
                        }
                    }

                    const line = Math.max(0, (issue.line || 1) - 1);
                    const col = Math.max(0, (issue.column || 1) - 1);
                    
                    // PRECISE RANGE (Word Picker): 
                    // If we have a snippet, use its length.
                    // Otherwise, try to find the word at the position in the document.
                    let range: vscode.Range;
                    if (issue.snippet && issue.snippet.length > 0) {
                        range = new vscode.Range(line, col, line, col + issue.snippet.length);
                    } else {
                        // Word Picker Fallback
                        const docLine = document.lineAt(line).text;
                        const wordRange = document.getWordRangeAtPosition(new vscode.Position(line, col));
                        if (wordRange) {
                            range = wordRange;
                        } else {
                            range = new vscode.Range(line, col, line, col + 1);
                        }
                    }
                    
                    let severity = vscode.DiagnosticSeverity.Information;
                    if (issue.severity === 'Critical' || issue.severity === 'Error') severity = vscode.DiagnosticSeverity.Error;
                    else if (issue.severity === 'Warning') severity = vscode.DiagnosticSeverity.Warning;

                    const diagnostic = new vscode.Diagnostic(range, issue.description, severity);
                    diagnostic.code = issue.code;
                    diagnostic.source = 'GeneXus LSP (Elite)';
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

    private getPartName(uri: vscode.Uri): string {
        return this.fsProvider.getPart(uri);
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
