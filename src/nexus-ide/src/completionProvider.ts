import * as vscode from 'vscode';
import { nativeFunctions, keywords, typeMethods } from './gxNativeFunctions';

export class GxCompletionItemProvider implements vscode.CompletionItemProvider {
    private varCache = new Map<string, any[]>();

    constructor(private readonly callGateway: (cmd: any) => Promise<any>) {}

    async provideCompletionItems(
        document: vscode.TextDocument,
        position: vscode.Position,
        _token: vscode.CancellationToken,
        context: vscode.CompletionContext
    ): Promise<vscode.CompletionItem[] | vscode.CompletionList> {
        const items: vscode.CompletionItem[] = [];
        const lineText = document.lineAt(position).text;
        const lineUntilCursor = lineText.substring(0, position.character);

        // 1. Check for Member Access (e.g., &var. or &var.pa)
        const memberMatch = lineUntilCursor.match(/&([a-zA-Z0-9_]+)\.([a-zA-Z0-9_]*)$/);
        if (memberMatch) {
            const varName = memberMatch[1];
            const partial = memberMatch[2];
            const objName = this.getObjName(document);
            const variables = await this.getVariables(objName);
            const variable = variables.find(v => v.name.toLowerCase() === varName.toLowerCase());
            
            if (variable) {
                let type = variable.type;
                const isCollection = type.endsWith('Collection');
                if (isCollection) type = 'Collection';

                // SDT / Transaction Structure Completion
                if (type !== 'Character' && type !== 'Numeric' && type !== 'Date' && type !== 'DateTime' && type !== 'Boolean') {
                    try {
                        const structure = await this.callGateway({
                            method: 'execute_command',
                            params: { module: 'Structure', action: 'Get', target: type }
                        });
                        if (structure && structure.fields) {
                            for (const field of structure.fields) {
                                if (partial && !field.name.toLowerCase().startsWith(partial.toLowerCase())) continue;
                                const item = new vscode.CompletionItem(field.name, vscode.CompletionItemKind.Field);
                                item.detail = `(Field) ${field.type}${field.isCollection ? ' Collection' : ''}`;
                                item.sortText = `000_${field.name}`; // Fields first
                                items.push(item);
                            }
                        }
                    } catch (e) {
                        console.error("[Nexus IDE] SDT Structure error:", e);
                    }
                }

                // Standard Methods
                const methods = typeMethods[type] || typeMethods['Character'];
                for (const m of methods) {
                    if (partial && !m.name.toLowerCase().startsWith(partial.toLowerCase())) continue;
                    const item = new vscode.CompletionItem(m.name, vscode.CompletionItemKind.Method);
                    item.detail = `${m.name}${m.parameters}: ${m.returnType}`;
                    item.documentation = new vscode.MarkdownString(m.description);
                    item.insertText = new vscode.SnippetString(m.snippet || m.name);
                    item.sortText = `002_${m.name}`; // Methods after fields
                    items.push(item);
                }
            }
            return items;
        }

        // 2. Context Detection: Are we inside a For Each? Detection of Base Table.
        let isInsideForEach = false;
        let baseTable: string | undefined;
        for (let i = position.line; i >= 0; i--) {
            const line = document.lineAt(i).text;
            const forEachMatch = line.match(/for each\s+([a-zA-Z0-9_]+)/i);
            if (forEachMatch) {
                isInsideForEach = true;
                baseTable = forEachMatch[1];
                break;
            }
            if (line.toLowerCase().includes('for each')) {
                isInsideForEach = true;
                break;
            }
            if (line.toLowerCase().includes('endfor') && i !== position.line) break;
        }

        // 3. Native Functions and Keywords
        for (const func of nativeFunctions) {
            const item = new vscode.CompletionItem(func.name, vscode.CompletionItemKind.Function);
            item.detail = `(Native) ${func.name}${func.parameters}`;
            item.documentation = new vscode.MarkdownString(`${func.description}\n\n**Example:** \`${func.example}\``);
            item.insertText = new vscode.SnippetString(func.snippet || func.name);
            item.sortText = `003_${func.name}`;
            items.push(item);
        }

        for (const kw of keywords) {
            const item = new vscode.CompletionItem(kw.name, vscode.CompletionItemKind.Snippet);
            item.insertText = new vscode.SnippetString(kw.snippet);
            items.push(item);
        }

        // 4. Local Variables
        const objName = this.getObjName(document);
        const variables = await this.getVariables(objName);
        for (const v of variables) {
            const item = new vscode.CompletionItem(`&${v.name}`, vscode.CompletionItemKind.Variable);
            item.detail = `${v.type}(${v.length})`;
            item.sortText = `001_${v.name}`; // Variables after fields
            items.push(item);
        }

        // 5. Attributes (Prioritize if in For Each)
        const range = document.getWordRangeAtPosition(position);
        if (range || isInsideForEach) {
            const word = range ? document.getText(range) : "";
            // If in For Each, we search even with 0 chars or small prefix
            if (isInsideForEach || (word.length >= 2 && !word.startsWith('&'))) {
                const attrResults = await this.callGateway({
                    method: 'execute_command',
                    params: { module: 'Search', query: `type:Attribute ${word}`, limit: isInsideForEach ? 30 : 15 }
                });
                if (attrResults && attrResults.results) {
                    for (const attr of attrResults.results) {
                        const item = new vscode.CompletionItem(attr.name, vscode.CompletionItemKind.Property);
                        const typeInfo = `${attr.dataType}(${attr.length}${attr.decimals > 0 ? ',' + attr.decimals : ''})`;
                        item.detail = `(Attribute) ${typeInfo}`;
                        
                        const doc = new vscode.MarkdownString();
                        if (attr.description) doc.appendMarkdown(`*${attr.description}*\n\n`);
                        if (attr.table) doc.appendMarkdown(`**Base Table:** ${attr.table}\n\n`);
                        item.documentation = doc;

                        if (isInsideForEach) {
                            item.preselect = true;
                            // Even higher priority if it belongs to the detected base table
                            if (baseTable && attr.table?.toLowerCase() === baseTable.toLowerCase()) {
                                item.sortText = `000_000_${attr.name}`;
                                item.detail = `(Base Attribute) ${typeInfo}`;
                            } else {
                                item.sortText = `000_001_${attr.name}`;
                            }
                        }
                        items.push(item);
                    }
                }
                
                // Extra: Fetch attributes directly from base table if search didn't catch them
                if (baseTable && items.filter(it => it.detail?.includes('(Base Attribute)')).length === 0) {
                    try {
                        const directAttrs = await this.callGateway({
                            method: 'execute_command',
                            params: { module: 'Structure', action: 'GetTable', target: baseTable }
                        });
                        if (directAttrs && directAttrs.attributes) {
                            for (const attr of directAttrs.attributes) {
                                if (word && !attr.name.toLowerCase().startsWith(word.toLowerCase())) continue;
                                const item = new vscode.CompletionItem(attr.name, vscode.CompletionItemKind.Property);
                                item.detail = `(Base Attribute) ${attr.type}`;
                                item.sortText = `000_000_${attr.name}`;
                                item.preselect = true;
                                items.push(item);
                            }
                        }
                    } catch { }
                }
            }
        }

        return items;
    }

    private getObjName(document: vscode.TextDocument): string {
        const path = decodeURIComponent(document.uri.path.substring(1));
        return path.split('/').pop()!.replace('.gx', '');
    }

    private async getVariables(objName: string): Promise<any[]> {
        if (this.varCache.has(objName)) return this.varCache.get(objName)!;

        try {
            const result = await this.callGateway({
                method: "execute_command",
                params: { module: 'Read', action: 'GetVariables', target: objName }
            });
            if (result && Array.isArray(result)) {
                this.varCache.set(objName, result);
                return result;
            }
        } catch (e) {
            console.error("[Nexus IDE] Error fetching variables:", e);
        }
        return [];
    }
}
