import * as vscode from 'vscode';
import { nativeFunctions } from './gxNativeFunctions';

export class GxHoverProvider implements vscode.HoverProvider {
    constructor(private readonly callGateway: (cmd: any) => Promise<any>) {}

    async provideHover(
        document: vscode.TextDocument,
        position: vscode.Position,
        _token: vscode.CancellationToken
    ): Promise<vscode.Hover | undefined> {
        const range = document.getWordRangeAtPosition(position);
        if (!range) return undefined;
        const word = document.getText(range);

        // Filter out keywords or very small words to avoid noise
        if (word.length < 3) return undefined;

        // 1. Check Native Functions first (Fast, Local)
        const nativeFunc = nativeFunctions.find(f => f.name.toLowerCase() === word.toLowerCase());
        if (nativeFunc) {
            const markdown = new vscode.MarkdownString();
            markdown.appendMarkdown(`### (Native) **${nativeFunc.name}**\n\n`);
            markdown.appendMarkdown(`${nativeFunc.description}\n\n`);
            markdown.appendMarkdown(`**Syntax:** \`${nativeFunc.name}${nativeFunc.parameters}\`\n\n`);
            markdown.appendMarkdown(`**Returns:** \`${nativeFunc.returnType}\`\n\n`);
            if (nativeFunc.example) {
                markdown.appendMarkdown(`---\n**Example:**\n`);
                markdown.appendCodeblock(nativeFunc.example, 'genexus');
            }
            return new vscode.Hover(markdown);
        }

        // 2. Check for Local Variables (starts with &)
        if (word.startsWith('&')) {
            const varName = word.substring(1).toLowerCase();
            try {
                const path = decodeURIComponent(document.uri.path.substring(1));
                const objName = path.split('/').pop()!.replace('.gx', '');
                const variables = await this.callGateway({
                    method: "execute_command",
                    params: { module: 'Read', action: 'GetVariables', target: objName }
                });
                
                if (Array.isArray(variables)) {
                    const variable = variables.find(v => v.name.toLowerCase() === varName);
                    if (variable) {
                        const markdown = new vscode.MarkdownString();
                        markdown.appendMarkdown(`### (Variable) **&${variable.name}**\n\n`);
                        markdown.appendMarkdown(`**Type:** \`${variable.type}\`\n\n`);
                        return new vscode.Hover(markdown);
                    }
                }
            } catch (e) {
                console.error("[Nexus IDE] Error fetching variable hover:", e);
            }
        }

        try {
            // 3. Check if word is an Attribute (using specific Read:GetAttribute for rich data)
            const attrData = await this.callGateway({
                method: 'execute_command',
                params: { module: 'Read', action: 'GetAttribute', target: word }
            });

            if (attrData && !attrData.error) {
                const markdown = new vscode.MarkdownString();
                markdown.appendMarkdown(`### (Attribute) **${attrData.name}**\n\n`);
                if (attrData.description) markdown.appendMarkdown(`*${attrData.description}*\n\n`);
                markdown.appendMarkdown(`**Type:** \`${attrData.type}(${attrData.length}${attrData.decimals > 0 ? ',' + attrData.decimals : ''})\`\n\n`);
                if (attrData.table) {
                    markdown.appendMarkdown(`**Base Table:** [${attrData.table}](https://genexus.com/search?q=${attrData.table})\n\n`);
                }
                return new vscode.Hover(markdown);
            }

            // 4. Fallback: Search for the object in the index (Remote)
            const result = await this.callGateway({
                method: 'execute_command',
                params: { module: 'Search', query: word, limit: 1 }
            });

            if (result && result.results && result.results.length > 0) {
                const obj = result.results[0];
                if (obj.name.toLowerCase() === word.toLowerCase()) {
                    const markdown = new vscode.MarkdownString();
                    markdown.appendMarkdown(`### [${obj.type}] **${obj.name}**\n\n`);
                    if (obj.description) markdown.appendMarkdown(`*${obj.description}*\n\n`);
                    if (obj.parm) {
                        markdown.appendCodeblock(obj.parm, 'genexus');
                    }
                    if (obj.snippet) {
                        markdown.appendMarkdown(`---\n**Preview:**\n`);
                        markdown.appendCodeblock(obj.snippet, 'genexus');
                    }
                    return new vscode.Hover(markdown);
                }
            }
        } catch (e) {
            console.error("[Nexus IDE] Hover error:", e);
        }

        return undefined;
    }
}
