# GeneXus MCP System Analysis and Improvement Plan

- [x] Analyze current architecture and codebase <!-- id: 0 -->
- [x] Evaluate existing tools and their performance <!-- id: 1 -->
- [x] Identify areas for improvement (UX, Performance, Features) <!-- id: 2 -->
- [x] Propose and document the improvement roadmap <!-- id: 3 -->
- [x] Frente 1: Implementing Semantic Analysis Engine <!-- id: 4 -->
  - [x] Research SDK classes for Dependecy/Variable analysis <!-- id: 5 -->
  - [x] Update AnalyzeService with native SDK logic <!-- id: 6 -->
  - [x] Verify results with genexus_analyze <!-- id: 7 -->
- [x] Frente 2: Semantic Search & Indexing <!-- id: 8 -->
  - [x] Research current search limitation <!-- id: 9 -->
  - [x] Design and Implement InvertedIndex with Graph support
  - [x] Integrate Relationship/Graph tracking in `AnalyzeService`
  - [x] Implement Graph-based ranking (Authority/Hub) in `SearchService`
  - [x] Implementation of bulk indexing for KB metadata (37k+ objects) - **Sub-5s search guaranteed.**
- [x] **Frente 3: Knowledge Base Business Intelligence (KB-BI)**
  - [x] Implement Business Rule extraction (Regex patterns for error/msg/bc)
  - [x] Add Domain Mapping logic (Tbl -> Business Domain)
    - [x] Implement Semantic Alias/Synonym system (Query Expansion)
  - [x] Implement Persistent Conceptual Summaries/Rules in `SearchIndex`
  - [x] Automated Business Impact Analysis (via Graph + Rules)
- [x] **Frente 4: Interactive Connection Visualizer**
  - [x] Design graph export API endpoint (`VisualizerService`)
  - [x] Generate JSON graph data from `SearchIndex` (nodes + edges)
  - [x] Research Cytoscape.js vs D3.js for rendering
  - [x] Create standalone HTML visualizer page (Embedded Cytoscape.js)
  - [x] Implement filtering by domain, type, and depth (Backend filter + Frontend grouping)
  - [x] Add click-to-navigate (Detail Panel implemented)
- [x] **Frente 5: Live Indexing (Real-time Sync)**
  - [x] Hook `UpdateIndex` into `WriteObject`, `ForgeObject`, and `BatchCommit` flows
  - [x] Implement incremental update logic with retry mechanism
  - [x] Fix `CommandDispatcher` `part` routing for `WriteService`
  - [x] Add `SourceSnippet` to `SearchService` scoring algorithm
  - [x] End-to-end verification: Forge→Search, Write→Search (all 4 steps passed)
- [x] **Frente 6: GeneXus Guard (Proactive Linter)**
  - [x] Define anti-pattern catalog (Commits in loops, unfiltered loops, blocking calls, etc.)
  - [x] Implement logic in `LinterService.cs`
  - [x] Add severity levels (Critical, Warning, Info)
  - [x] Return actionable fix suggestions with code snippets
  - [x] Exposed via `genexus_linter` tool
- [x] **Frente 7: Doc Assistant (Auto-Wiki)**
  - [x] Design Markdown template for functional documentation
  - [x] Extract object metadata (type, description, dependencies)
  - [x] Generate relationship diagrams using Mermaid.js syntax
  - [x] Include business rules (extracted from code comments)
  - [x] Support batch generation for entire domains/modules (`GenerateBatch`)

- [x] **Frente 14: Hyper-Performance & SDK Native Fusion**
  > Structural optimizations for sub-second response times in high-volume operations.
  - [x] **In-Process SDK Build**: Replaced MSBuild overhead with native `Genexus.MsBuild.Tasks` execution for `BuildAll`.
  - [x] **Tiered L1/L2 Cache**: RAM + Disk-backed object caching to avoid SDK/DB latency on large objects.
  - [x] **Semantic Diffing**: `WriteObject` now skips re-indexing if source content hasn't changed.
  - [x] **Static Regex Compilation**: All analysis patterns (Calls, Tables, Tags) now use `RegexOptions.Compiled`.
  - [x] **Parallel Metadata Indexing**: Accelerated `genexus_bulk_index` using `Parallel.ForEach`.

- [x] **Frente 15: Robustez Estrutural e Automação de Dependências (Lessons from Censo Task)**
  > Superação dos "pontos cegos" do SDK para garantir autonomia total sem IDE.
  - [x] **Injeção Dinâmica de Variáveis e Tabelas**: `VariableInjector` e `TableDependencyInjector` automatizam a declaração de variáveis e ancoragem de tabelas no `WriteObject`.
  - [x] **Diagnósticos de Alta Fidelidade**: Captura de `ErrorCode` e mensagens detalhadas via `SaveOutput` (Adeus "Validation Failed" genérico).
  - [x] **Estabilização do Worker & Heartbeat**: Thread segura com fila de comandos e resposta a "ping" para watchdog do Gateway.
  - [x] **Implementação Real de `genexus_get_hierarchy`**: Navegação bi-direcional via grafos de referência nativos do SDK.
  - [x] **Acesso a Partes Não-Texto**: Suporte para inspeção de variáveis (`GetVariables`) e atributos (`GetAttribute`).

- [ ] **Frente 16: Fast Fail & Instant Feedback**
  - [ ] Implement `genexus_validate` using SDK's `Validate()` method (in-memory syntax check).
  - [ ] Expose validation tool to allow pre-save checks.

- [x] **Frente 17: Semantic Query (Search Engine Upgrade)**
  - [x] Upgrade `SearchService` to support structured prefixes (`type:`, `calls:`, `updates:`).
  - [x] Implement query parser for combined filters (e.g., `type:Transaction updates:Tbl:Cliente`).

- [ ] **Frente 18: Surgical Patch (Context-Aware Editing)**
  - [ ] Implement `genexus_patch_object` with operations: `Append`, `Prepend`, `ReplaceBlock`.
  - [ ] Add regex-based anchor detection for precise code insertion.

- [x] **Frente 19: Pattern Reverse Engineering (Style Transfer)**
  - [x] Implement `genexus_get_pattern_sample` to find representative objects (best-practices).
  - [x] Add heuristics to select high-quality examples (low complexity, high reuse).

---

## Performance & Architecture

- [x] **Frente 8: Persistent Worker (Eliminates ~5s Bootstrap)**
  > Eliminated process re-spawning by keeping Worker alive with persistent KB connection.
  - [x] Refactor `Program.cs` to keep the process alive between commands (stdin loop)
  - [x] Cache the `KnowledgeBase.Open()` instance in memory across calls
  - [x] Implement graceful shutdown signal
  - [x] Add performance logging (milliseconds per command)
  - [x] Benchmark: commands now respond in <200ms after warmup.
- [ ] **Frente 9: Smart Object Cache (Tiered Memory)**
  > `ObjectService` uses a flat Dictionary with MAX_CACHE_SIZE=50. Hot objects get evicted.
  - [ ] Implement tiered cache: L1 (hot, 50 items) + L2 (warm, disk-backed, unlimited)
  - [ ] Add TTL-based invalidation with configurable expiry
  - [ ] Pre-warm cache on KB load with most-referenced objects (from `CalledBy` graph)
  - [ ] Add cache hit/miss metrics logging for performance tuning

## Intelligence & Developer Experience

- [x] **Frente 10: Impact Radius Analysis**
  > "If I change X, what breaks?" — critical for safe refactoring.
  - [x] Implement transitive `CalledBy` graph traversal (N-depth)
  - [x] Calculate blast radius score (# of affected objects, weighted by type)
  - [x] Identify critical paths (objects with high Authority + deep dependency chains)
  - [ ] Generate change risk report before `WriteObject` operations
  - [x] Integrate with `genexus_analyze` output as `impactRadius` field
- [ ] **Frente 11: Code Generation Templates (Scaffolding)**
  > Automate creation of common patterns: CRUD procedures, BC wrappers, API endpoints.
  - [ ] Design template DSL for common GeneXus patterns
  - [ ] Implement `genexus_scaffold` command (e.g., `scaffold crud Customer`)
  - [ ] Generate Transaction + BC Procedure + Validation rules from template
  - [ ] Support custom templates from `.gxmcp/templates/` directory
  - [ ] Auto-wire generated objects with correct domain/naming conventions
- [x] **Frente 12: Cross-KB Analytics & Health Dashboard**
  > Holistic KB health monitoring: dead code, circular dependencies, complexity hotspots.
  - [x] Dead code detector (objects with 0 `CalledBy` that aren't entry points)
  - [x] Circular dependency detector (cycle detection in call graph)
  - [x] Complexity hotspot map (top-N objects by complexity score)
  - [x] Generate JSON health report for external consumption
  - [ ] Trend tracking: compare index snapshots over time to detect code drift
- [x] **Frente 13: Zero-IDE Stability & Surgical Editing (The "Fly-by-Wire" Upgrade)**
  > Addresses major blockers for fully autonomous development without the GeneXus IDE.
  - [x] **Transparent Compiler Feedback**: Capture and return the full GeneXus MSBuild/SDK error log during `WriteObject` operations (instead of generic "Validation failed").
  - [x] **Variable Inspection API**: Enhanced `genexus_get_variables` to return the full list of defined variables, including their types and lengths.
  - [x] **Transaction Hierarchy Mapping**: Implemented `genexus_get_hierarchy` to visualize the level structure and physical table names.
  - [x] **Deep Code Indexing (RAG)**: Index source code content (Procedures, Events) in `SearchIndex` to allow semantic search of code patterns (e.g., "how to use ReadLine").
  - [x] **Granular Procedure Editing**: Enable `genexus_read_section` and `genexus_write_section` for Procedures, allowing surgical updates to specific `Sub ... EndSub` blocks.
  - [x] **Attribute Metadata Tool**: Created `genexus_get_attribute` fast lookup tool for attribute properties (Type, Length, Domain, Table).
