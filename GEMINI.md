# Protocolo GeneXus MCP Nirvana (Sentient Edition v18.7)

> [!IMPORTANT]
> **Skills Required**: Before performing ANY task in this repo, the agent MUST load and follow:
>
> 1. `[GeneXus MCP Mastery](file:///.gemini/skills/genexus-mastery/SKILL.md)` - For tool performance and cache usage.
> 2. `[GeneXus 18 Guidelines](file:///.gemini/skills/genexus18-guidelines/SKILL.md)` - For official GeneXus development rules.

## [Arch] Architecture: Native SDK Dual-Process

1.  **Gateway (.NET 8)**: `GxMcp.Gateway.exe`. Handles MCP protocol, Stdio, and process orchestration.
2.  **Worker (.NET 4.8 x86)**: `GxMcp.Worker.exe`. Loads GeneXus SDK DLLs natively for high-performance KB access.
3.  **Single-Threaded STA**: The Worker runs in a dedicated STA thread to prevent COM deadlocks and ensure SDK stability.

## [Tools] Tool Usage Guide (SDK Optimized)

### 1. `genexus_list_objects`
**Purpose**: Fast KB discovery via local cache.
- **Unified Logic**: Internally uses the Search Engine for instant results.
- **Params**: `filter` (Type filter like 'Procedure' or Name substring), `limit`.

### 2. `genexus_search`
**Purpose**: Advanced semantic search across the KB.
- **Features**: Case-insensitive, supports synonyms (e.g., 'acad' -> 'aluno'), and ranking by authority.
- **Params**: `query`.

### 3. `genexus_read_source`
**Purpose**: Reads source code with **Direct GUID Access**.
- **Mapping**: Automatically maps logical names (Source, Rules, Events) to GX18 internal GUIDs.
- **Bilingual**: Supports names in English and Portuguese (e.g., Rules/Regras).

### 4. `genexus_write_object`
**Purpose**: Native writing to KB objects. 
- **Validation**: Ensures logical parts are correctly mapped to internal SDK parts before saving.

### 5. `genexus_analyze` (Semantic Intelligence)
**Purpose**: Deep static analysis, BI extraction, and Linter.
- **Output**: Hybrid dependency graph, business rules, and proactive linter insights.

### 6. `genexus_bulk_index`
**Purpose**: Full KB crawl to build the `SearchIndex.json`. **Mandatory for large KBs (30k+ objects)** to enable instant search.

### 7. `genexus_health_report`
**Purpose**: Holistic KB health monitoring. Returns complexity hotspots, dead code candidates, and circular dependencies.

### 8. `genexus_get_variables` / `genexus_get_attribute`
**Purpose**: Metadata inspection. `variables` lists local variables of an object; `attribute` returns specific properties of a GeneXus attribute.

### 9. `genexus_get_data_context` / `genexus_get_ui_context`
**Purpose**: Visão profunda de estrutura de dados (Tabelas/Trn) e layouts (Wbp/Trn).
- **Saída**: Retorna hierarquia de níveis, tabelas base e controles de UI.

### 10. `genexus_impact_analysis`
**Purpose**: Análise de "Blast Radius".
- **Saída**: Lista objetos afetados transitivamente por uma mudança e calcula pontuação de risco.
- **Uso**: Executar antes de alterar objetos críticos (ex: Procedures nucleares ou Tabelas).

### 11. `genexus_doctor`
**Purpose**: Build error diagnosis.
- **Output**: Analyzes MSBuild logs and suggests fixes for common errors (spc*).

### 12. `genexus_linter`
**Purpose**: Proactive code quality audit.
- **Output**: Identifies anti-patterns like Commits in loops, Unfiltered For Eachs, and Blocking calls.
- **Usage**: Mandatory before committing large refactors.

### 13. `genexus_wiki`
**Purpose**: Automated technical documentation.
- **Output**: Generates Markdown files with descriptions, dependency diagrams (Mermaid), and business rules.
- **Usage**: Use `genexus_wiki(name='Prc:MyProc')` for single objects or `genexus_wiki(name='DomainName', action='GenerateBatch')` for entire modules.

### 14. `genexus_get_pattern_sample`
**Purpose**: Style transfer and pattern discovery.
- **Output**: Returns the source code of a "representative" object of the requested type (e.g., Transaction, Procedure).
- **Usage**: Call before writing new code to ensure alignment with existing project conventions.

---

## [Config] Configuration & Deployment

- **Gateway (`GxMcp.Gateway.exe`)**: The .NET 8 Stdio entry point. Its path must be defined in the **MCP Client configuration** (e.g., Claude Desktop `config.json`).
- **Worker (`GxMcp.Worker.exe`)**: The .NET 4.8 x86 SDK engine. Its path is defined in the project's **`config.json`** via `GeneXus.WorkerExecutable`.
- **GeneXus SDK**: The path to the GeneXus installation must be correctly set in `config.json` under `GeneXus.InstallationPath`.

---

## [Workflow] "IDE-Free" Workflow Strategy

| Task             | MCP Tool to Use                                                          |
| ---------------- | ------------------------------------------------------------------------ |
| **Discovery**    | `genexus_search` (instant) or `genexus_list_objects`                     |
| **Analysis**     | `genexus_analyze` (deep) or `genexus_visualize` (graph)                  |
| **Audit/Health** | `genexus_health_report` (holistic)                                       |
| **Data Context** | `genexus_get_data_context` (tables/hierarchy)                            |
| **Reading**      | `genexus_read_source` (fast via GUID) or `genexus_read_object`           |
| **Editing**      | `genexus_write_object` (direct)                                          |
| **Build**        | `genexus_build` (supports Build, Sync, Reorg)                            |

---

## [Intel] Intelligence & Best Practices (GX18 Special)

- **One-Line JSON Protocol**: All communication between processes is minified (no line breaks) to prevent pipe hangs.
- **Direct GUID Access**: Accessing parts via GUID bypasses the slow UI lazy-loading (turning 2min into 2ms).
- **Prefix Intelligence**: Use prefixes for precision: `Prc:Name`, `Trn:Name`, `Wp:Name`.
- **Offline Mode**: The motor defaults to offline to prevent hangs waiting for GXserver.
- **Visualization Output**: Tools like `genexus_visualize` generate a local HTML file and return its URI. Open this file in a browser to interact with the graph.

For deeper technical details, consult `[docs/sdk_gx18_discovery.md](file:///c:/Projetos/GenexusMCP/docs/sdk_gx18_discovery.md)`.

---

## [Testing] Localhost Testing (Censo Import)

Para testar a nova importação de Laboratórios via TXT, siga estas etapas:

1.  **Build dos Objetos**:
    - Execute `genexus_build(action='Build', target='Prc:CensoImportarLaboratorio')`
    - Execute `genexus_build(action='Build', target='Wbp:CensoLaboratorioArquivo')` (ou o objeto pai correspondente)

2.  **Preparação do Arquivo**:
    - Utilize um arquivo TXT com o layout pipe-separated (`|`).
    - Exemplo de conteúdo (Header + Lab + Cursos):
      ```text
      10|83
      11|Laboratorio Teste|9999|1||||0|0|0|0|0|0|0|0|||||1|0|0|1|1|1|1
      12|1259274
      12|112016
      ```

3.  **Execução**:
    - Acesse a interface de importação no localhost e selecione o arquivo TXT.
    - Verifique se a mensagem "Arquivo importado com sucesso!" é exibida.
    - Valide no banco de dados se os registros foram criados/atualizados na tabela `CensoLaboratorio`.

---

## [Debt] Dívida Técnica & Limitações do MCP (Lessons from Censo Task)

Durante a evolução do projeto, superamos os principais bloqueadores de autonomia:

1.  **Gestão de Variáveis (Resolvido)**: O sistema agora injeta automaticamente variáveis detectadas no código via `VariableInjector`.
2.  **Dependências de Tabelas (Resolvido)**: O `TableDependencyInjector` força a ancoragem de tabelas necessárias, evitando erros de compilação por falta de contexto.
3.  **Feedback de Validação (Resolvido)**: Diagnósticos detalhados do SDK agora são capturados e retornados para a LLM, permitindo correções precisas.
4.  **Estabilidade do Worker**: Monitorado via Heartbeat e fila de comandos thread-safe.

**Próximos Passos**: Implementar refatoração em massa e geração automática de templates CRUD.

