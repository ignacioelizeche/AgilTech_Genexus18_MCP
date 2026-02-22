import * as vscode from 'vscode';
import { nativeFunctions } from './gxNativeFunctions';

export class GxSignatureHelpProvider implements vscode.SignatureHelpProvider {
    constructor(private readonly callGateway: (cmd: any) => Promise<any>) {}

    async provideSignatureHelp(
        document: vscode.TextDocument,
        position: vscode.Position,
        _token: vscode.CancellationToken,
        _context: vscode.SignatureHelpContext
    ): Promise<vscode.SignatureHelp | undefined> {
        const lineText = document.lineAt(position).text;
        const lineUntilCursor = lineText.substring(0, position.character);

        // Match patterns like MyProc( or &var.Method( or even just (
        // We look for the last open parenthesis that isn't closed
        const lastParenIndex = lineUntilCursor.lastIndexOf('(');
        if (lastParenIndex === -1) return undefined;

        const prefix = lineUntilCursor.substring(0, lastParenIndex).trim();
        const match = prefix.match(/([a-zA-Z0-9_]+)$/);
        if (!match) return undefined;

        const name = match[1];
        const paramsText = lineUntilCursor.substring(lastParenIndex + 1);
        const paramIndex = (paramsText.match(/,/g) || []).length;

        // 1. Check Native Functions
        const native = nativeFunctions.find(f => f.name.toLowerCase() === name.toLowerCase());
        if (native) {
            const sig = new vscode.SignatureHelp();
            const si = new vscode.SignatureInformation(native.name + native.parameters, native.description);
            if (native.paramDetails) {
                si.parameters = native.paramDetails.map(p => new vscode.ParameterInformation(p.trim()));
            }
            sig.signatures = [si];
            sig.activeSignature = 0;
            sig.activeParameter = Math.min(paramIndex, si.parameters.length - 1);
            return sig;
        }

        // 2. KB Object Search
        try {
            const result = await this.callGateway({
                method: 'execute_command',
                params: { module: 'Analyze', action: 'GetParameters', target: name }
            });

            if (result && result.parameters) {
                const sig = new vscode.SignatureHelp();
                const paramStr = result.parameters.map((p: any) => `${p.direction ? p.direction + ':' : ''}${p.accessor}`).join(', ');
                const label = `${result.name}(${paramStr})`;
                const si = new vscode.SignatureInformation(label, `(GeneXus ${result.type})`);
                si.parameters = result.parameters.map((p: any) => new vscode.ParameterInformation(p.accessor, `${p.direction || ''} ${p.type || ''}`));
                sig.signatures = [si];
                sig.activeSignature = 0;
                sig.activeParameter = Math.min(paramIndex, si.parameters.length - 1);
                return sig;
            }
        } catch (e) {
            console.error("[Nexus IDE] Signature Help error:", e);
        }

        return undefined;
    }
}
