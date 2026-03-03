# Protocolo GeneXus 18 MCP (v1.0.0)

> [!IMPORTANT]
> **Skills Required**: Before performing ANY task in this repo, the agent MUST load and follow:
>
> 1. `[GeneXus MCP Mastery](file:///.gemini/skills/genexus-mastery/SKILL.md)` - For tool performance and cache usage.
> 2. `[GeneXus 18 Guidelines](file:///.gemini/skills/genexus18-guidelines/SKILL.md)` - For official GeneXus development rules.

## 🏎️ Performance & Infrastructure (v1.0.0)

O MCP opera com arquitetura desacoplada e alta performance:

1.  **Zero Hardcoding**: Caminhos de instalação (`GX_PROGRAM_DIR`) e KBs (`GX_KB_PATH`) são resolvidos via Variáveis de Ambiente ou `config.json`.
2.  **Hot Reload (Config Watcher)**: O Gateway monitora o `config.json`. Alterações na KB ou caminhos reiniciam o Worker automaticamente.
3.  **Tool Registry Dinâmico**: As definições de ferramentas residem em `tool_definitions.json`.
4.  **PartAccessor (Standard)**: Acesso padronizado a partes de objetos (Source, Rules, Variables) via `PartAccessor.cs`.
5.  **Base64 Pipeline**: Transporte binário 100% imune a encoding/acentuação.

## 🔍 Intelligence: Contexto Profundo

1.  **Injeção Recursiva**: `genexus_inject_context(recursive: true)` identifica dependências de dependências (ex: Procedures -> SDTs -> Domínios), injetando o grafo completo no contexto da IA.
2.  **Extração de Domínios/Enums**: O sistema resolve Domínios e Enums automaticamente, permitindo que a IA entenda constantes do negócio.
3.  **Business Components (BC)**: Injeção automática da estrutura de BCs ao analisar transações.
4.  **Fallback Discovery**: Identificação de tabelas e referências via nomes e padrões quando a navegação nativa não está disponível.

## 🛠️ Integrated Experience: Nexus-IDE (VS Code)

1.  **Sincronização Ativa**: A IDE e o MCP compartilham o mesmo `config.json`.
2.  **Virtual FS**: Acesso via `genexus:/[Type]/[Name]` em VS Code.
3.  **ALWAYS COMPILE**: Após qualquer mudança em C#, execute `.\build.ps1`.

## [Tools] Elite Tool Usage Guide

### 1. `genexus_patch` (Surgical Edit) - **PREFER THIS**
Surgical line-by-line replacement using context. Preserva indentação e whitespace.

### 2. `genexus_inject_context` (Deep Context)
**CRITICAL**: Use antes de implementar lógica complexa para garantir que todas as dependências (SDTs, Procedures chamadas) estejam no contexto.

### 3. `genexus_analyze(mode="navigation")`
Obtém o plano de execução nativo da GeneXus (Tabelas, Índices, Filtros).

---

## ⌨️ Shell & Automation: Anti-Mistake Protocol (v19.1)

> [!IMPORTANT]
> **CRITICAL RULE**: NEVER use the `cd` command within a `run_command` string. Always use the `Cwd` parameter.

1.  **Command Separators**: No Windows (PowerShell), use `;` em vez de `&&`.
2.  **Build Verification**: Após alterações estruturais, rode `.\build.ps1` e verifique se há 0 erros.
