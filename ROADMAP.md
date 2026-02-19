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
  - [x] Implementation of bulk indexing for KB metadata (37k+ objects)
- [x] **Frente 3: Knowledge Base Business Intelligence (KB-BI)**
  - [x] Implement Business Rule extraction (Regex patterns for error/msg/bc)
  - [x] Add Domain Mapping logic (Tbl -> Business Domain)
    - [x] Implement Semantic Alias/Synonym system (Query Expansion)
  - [x] Implement Persistent Conceptual Summaries/Rules in `SearchIndex`
  - [x] Automated Business Impact Analysis (via Graph + Rules)
- [ ] **Frente 4: Interactive Connection Visualizer**
  - [ ] Design graph export API endpoint (`VisualizerService`)
  - [ ] Generate JSON graph data from `SearchIndex` (nodes + edges)
  - [ ] Research Cytoscape.js vs D3.js for rendering
  - [ ] Create standalone HTML visualizer page
  - [ ] Implement filtering by domain, type, and depth
  - [ ] Add click-to-navigate (link back to `genexus_read_object`)
- [x] **Frente 5: Live Indexing (Real-time Sync)**
  - [x] Hook `UpdateIndex` into `WriteObject`, `ForgeObject`, and `BatchCommit` flows
  - [x] Implement incremental update logic with retry mechanism
  - [x] Fix `CommandDispatcher` `part` routing for `WriteService`
  - [x] Add `SourceSnippet` to `SearchService` scoring algorithm
  - [x] End-to-end verification: Forge→Search, Write→Search (all 4 steps passed)
- [ ] **Frente 6: GeneXus Guard (Proactive Linter)**
  - [ ] Define anti-pattern catalog (N+1 queries, unused variables, empty catches, etc.)
  - [ ] Implement rule engine in `AnalyzeService` quality checks
  - [ ] Add severity levels (Error, Warning, Info)
  - [ ] Return actionable fix suggestions with line references
  - [ ] Integrate with `genexus_refactor` for auto-fix capabilities
- [ ] **Frente 7: Doc Assistant (Auto-Wiki)**
  - [ ] Design Markdown template for functional documentation
  - [ ] Extract object metadata (type, description, attributes, dependencies)
  - [ ] Generate relationship diagrams (Mermaid syntax)
  - [ ] Include business rules and domain context from `SearchIndex`
  - [ ] Support batch generation for entire modules/domains

---

## Performance & Architecture

- [ ] **Frente 8: Persistent Worker (Eliminates ~5s Bootstrap)**
  > Currently every command spawns a new `GxMcp.Worker.exe` process, re-bootstrapping the SDK (~5s).
  > This is the single biggest performance bottleneck.
  - [ ] Refactor `Program.cs` to keep the process alive between commands (stdin loop)
  - [ ] Cache the `KnowledgeBase.Open()` instance in memory across calls
  - [ ] Implement graceful shutdown signal (e.g., `{"method": "shutdown"}`)
  - [ ] Add health-check/ping command for Gateway process monitoring
  - [ ] Benchmark: target <100ms per command (vs current ~5-8s)
- [ ] **Frente 9: Smart Object Cache (Tiered Memory)**
  > `ObjectService` uses a flat Dictionary with MAX_CACHE_SIZE=50. Hot objects get evicted.
  - [ ] Implement tiered cache: L1 (hot, 50 items) + L2 (warm, disk-backed, unlimited)
  - [ ] Add TTL-based invalidation with configurable expiry
  - [ ] Pre-warm cache on KB load with most-referenced objects (from `CalledBy` graph)
  - [ ] Add cache hit/miss metrics logging for performance tuning

## Intelligence & Developer Experience

- [ ] **Frente 10: Impact Radius Analysis**
  > "If I change X, what breaks?" — critical for safe refactoring.
  - [ ] Implement transitive `CalledBy` graph traversal (N-depth)
  - [ ] Calculate blast radius score (# of affected objects, weighted by type)
  - [ ] Identify critical paths (objects with high Authority + deep dependency chains)
  - [ ] Generate change risk report before `WriteObject` operations
  - [ ] Integrate with `genexus_analyze` output as `impactRadius` field
- [ ] **Frente 11: Code Generation Templates (Scaffolding)**
  > Automate creation of common patterns: CRUD procedures, BC wrappers, API endpoints.
  - [ ] Design template DSL for common GeneXus patterns
  - [ ] Implement `genexus_scaffold` command (e.g., `scaffold crud Customer`)
  - [ ] Generate Transaction + BC Procedure + Validation rules from template
  - [ ] Support custom templates from `.gxmcp/templates/` directory
  - [ ] Auto-wire generated objects with correct domain/naming conventions
- [ ] **Frente 12: Cross-KB Analytics & Health Dashboard**
  > Holistic KB health monitoring: dead code, circular dependencies, complexity hotspots.
  - [ ] Dead code detector (objects with 0 `CalledBy` that aren't entry points)
  - [ ] Circular dependency detector (cycle detection in call graph)
  - [ ] Complexity hotspot map (top-N objects by complexity score)
  - [ ] Generate JSON health report for external consumption
  - [ ] Trend tracking: compare index snapshots over time to detect code drift
