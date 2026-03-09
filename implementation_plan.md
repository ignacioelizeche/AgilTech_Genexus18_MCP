# Análise Holística e Otimização do Ecossistema GeneXus MCP
## Relatório de Diagnóstico ("Audit & Evolve")

Este relatório apresenta os resultados de uma auditoria completa na arquitetura do `GenexusMCP`, abordando seus componentes principais: o Worker em C#, o Gateway Node.js/TS, e a interface IDE. A análise foca em Qualidade de Contexto, Performance, Resiliência, e Developer Experience.

### 1. Fluxo de Edição Cirúrgica (`genexus_patch`)
O fluxo completo para uma modificação parcial (patch) de um objeto GeneXus percorre as seguintes etapas:
- **IDE (VS Code)**: Um desenvolvedor ou agente IA invoca a ferramenta MCP `genexus_edit` com `mode=patch`.
- **Gateway (`ObjectRouter.cs`)**: O request JSON é processado e convertido num objeto interno contendo `module="Patch"` e `action="Apply"`. O roteador encapsula a requisição mantendo metadados cruciais.
- **Gateway -> Worker (`WorkerProcess.cs`)**: O Gateway enfileira a requisição (Task/Channel) e a despacha como JSON-RPC (payload em Base64) via Standard Input (`Stdio`) para o processo .NET (`GxMcp.Worker.exe`).
- **Worker (`CommandDispatcher.cs`)**: Desserializa a chamada JSON-RPC e delega para o `PatchService`.
- **Worker (`PatchService.cs`)**:
  - Lê a estrutura atual do objeto (ex: source ou rules).
  - Executa uma verificação anti-ambiguidade (valida se o texto a substituir aparece uma única vez).
  - Aplica o patch textual na cópia em memória.
- **Worker (`WriteService.cs`)**:
  - Encaminha o novo código gerado ao `ValidationService.cs` para testes de sintaxe (Auto-Healing com mock transactions).
  - Se aprovado, assinala a estrutura em memória como dirty, usa o SDK da GeneXus (`obj.EnsureSave()`) para salvar e executa o `transaction.Commit()`.
  - Notifica o Gateway do sucesso, agendando indexação de retaguarda (Fast Save).

### 2. Critical Fixes (Bugs latentes e performance)
*   **Problemas com "Ghost Folders" na IDE:** O `addKbFolder` tenta forçar o mount virtual de `gxkb18:/`, porém, se o Worker demorar a responder, o mount falha silenciosamente. Necessário um Retry-Policy robusto.
*   **Gestão de Recursos / Memory Leaks:** A comunicação via Standard Output possui um histórico de overflow em payloads muito grandes. Há paginação nos Reads, mas edições pesadas podem travar o pipe do JSON-RPC. A verificação anti-deadlock no Gateway `WorkerProcess.cs` que reseta o Worker (após 45s) é uma solução extrema ("slop"); é recomendável identificar o que congela a SDK (provavelmente locks do banco SQLite/SQL local do KB).
*   **Vazamento de Commits Pendentes:** O "Fast Save" (`WriteService.cs`) aciona um timer para flush em background. Um reinício súbito ou erro fatal pode perder gravações pendentes no KB.

### 3. Architectural Debt (Acoplamento e Limitações)
*   **Acoplamento com Texto:** O `PatchService` atualmente funciona na base de substituição de strings em texto (`Replace`). Sendo um MCP acoplado nativamente a uma AST e DSL (GeneXus SDK), as modificações deveriam usar preferencialmente a injeção em nós ou árvores semânticas em vez de "hacks" de texto.
*   **Sobrecarga do `WorkerProcess.cs`:** O mecanismo de HeathCheck e interrupção do Worker mistura responsabilidades operacionais e de rede no Gateway. A reescrita ideal delegaria o lifecycle do host local do Worker para um sidecar mais limpo, talvez via gRPC em invés de stdio.

### 4. Elite Enhancements (Propostas State-of-the-art)
*   **Análise Vetorial Nativa e Caching Semântico:** Melhorar as ferramentas `genexus_analyze` e `genexus_inject_context` mapeando as relações do KB com representações vetoriais de impacto (`BlastRadius`). Para evitar estourar o limite de tokens dos LLMs, implemente "Semantic Compression" por padrão, apenas injetando `parameters` e `SDT Structure` dos nós imediatos.
*   **Inteligência Contextual Nível-AST:** Ao invés do `PatchService` procurar por strings (`IndexOf`), expor a funcionalidade de modificação via DSL Parser da GeneXus (por exemplo, manipulando diretamente `ISymbol` ou objetos `Artech.Genexus.Common.Parts`).
*   **DX – Sugestões "Smart" do `GxCompletionItemProvider`:** O Autocomplete de VS Code atual injeta os detalhes baseado em expressões regulares (ex: detecção de `for each BaseTable`). Isso pode ser elevado permitindo que o completion capture a AST da linha corrente direto do Worker (via `GetContext`) para inferir tipos mais precisos (ex: métodos válidos em SDTs).
