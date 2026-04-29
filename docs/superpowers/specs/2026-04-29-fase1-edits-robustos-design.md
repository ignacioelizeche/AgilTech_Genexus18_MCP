# Fase 1 — Edits robustos e enxutos (v2.0.0)

**Date:** 2026-04-29
**Bucket:** A (token/round-trips reduction) + D (robustness)
**Target release:** v2.0.0 (major bump — breaking changes)

## Goals

Reduce token surface and round-trips for AI agents while making write operations safer and idempotent. Four coordinated changes:

1. Tool consolidation (remove `batch_*` duplicates).
2. Hybrid diff edits (`xml` | `ops` | `patch`).
3. Uniform `dryRun` flag with standardized `plan` schema.
4. Idempotency keys for write tools.

Out of scope (Fase 2): schema export CLI, token budget guard with stable cursor.

## Non-goals

- No new MCP tools added. Existing tool surface only shrinks or gets richer args.
- No persistence layer for idempotency cache (in-memory only).
- No automatic XML→ops migration tool.

## Background

Repo state (commits as of 2026-04-29):

- `c71a21c` v1.3.1
- `3c4b09a` introduced partial `dryRun` and structural XML compare
- `b1d0dcc` paginated list/search responses, handshake contract tests

Existing tool surface in `README.md`:
- Reads: `genexus_read`, `genexus_batch_read`, `genexus_query`, `genexus_inspect`, `genexus_list_objects`, `genexus_properties`
- Writes: `genexus_edit`, `genexus_batch_edit`, `genexus_create_object`, `genexus_refactor`, `genexus_forge`, `genexus_import_object`
- Response meta: `meta.schemaVersion = mcp-axi/1`

This spec bumps `meta.schemaVersion` to `mcp-axi/2`.

---

## 1. Tool consolidation

### Removed tools (v2.0.0 hard removal)

- `genexus_batch_read`
- `genexus_batch_edit`

### Replacement contract

`genexus_read`:
- `target: string` (singular form, returns object)
- `targets: string[]` (plural form, returns array, sets `meta.batched=true`)
- Mutually exclusive. Both absent = `usage_error`.

`genexus_edit`:
- `target: string` + one of (`xml` | `ops` | `patch`) — single object edit
- `targets: EditRequest[]` where `EditRequest = { target, xml? | ops? | patch? }` — batch
- Mutually exclusive at top level.

### Internal refactor

- Existing batch handlers become private dispatchers behind `read.handle(targets[])` and `edit.handle(targets[])`. Single-target calls wrap into `[target]` internally. Zero duplicated logic.

### Handshake signal

`initialize` response `meta.removedTools`:
```json
{ "removedTools": [
  { "name": "genexus_batch_read", "replacedBy": "genexus_read", "argHint": "use targets[]" },
  { "name": "genexus_batch_edit", "replacedBy": "genexus_edit", "argHint": "use targets[]" }
]}
```

### Migration

- `CHANGELOG.md` Breaking Changes section.
- `README.md` tool surface list updated.
- No deprecation aliases. Calling removed tool returns JSON-RPC `-32601 Method not found` with `data.replacedBy` for agent self-correction.

---

## 2. Hybrid diff edits

`genexus_edit` and `genexus_edit.targets[]` items accept exactly one of three payload modes.

### Mode `xml` (existing, unchanged)

```json
{ "target": "Customer", "xml": "<Transaction>...</Transaction>" }
```

### Mode `ops` (new, primary path for agents)

```json
{ "target": "Customer", "ops": [
  { "op": "set_attribute", "name": "Name", "type": "Character(60)" },
  { "op": "add_rule", "text": "Error('x') if Name.IsEmpty();" },
  { "op": "remove_event", "name": "Start" }
]}
```

Semantic op catalog (initial set — extensible):

**Transaction:**
- `set_attribute { name, type, formula?, default?, nullable? }`
- `add_attribute { name, type, position?, ...same fields }`
- `remove_attribute { name }`
- `rename_attribute { from, to }`
- `set_rule { index|match, text }`
- `add_rule { text, position? }`
- `remove_rule { index|match }`
- `set_event { name, code }`
- `add_event { name, code }`
- `remove_event { name }`

**Procedure / DataProvider:**
- `set_source { code }`
- `set_rules { text }`
- `set_variable { name, type, ...properties }`
- `add_variable { name, type, ...properties }`
- `remove_variable { name }`

**WebPanel / Panel:**
- `set_event { name, code }`
- `set_layout_property { controlId, property, value }` — delegates to `genexus_layout` internally

**Generic:**
- `set_property { path, value }` — top-level object property (e.g., `Description`, `Module`)

Each op has a JSON Schema validated before dispatch. Unknown op → `usage_error` with `path: ops[i].op`.

### Mode `patch` (new, fallback)

```json
{ "target": "Customer", "patch": [
  { "op": "replace", "path": "/rules/0/text", "value": "..." }
]}
```

RFC 6902 over canonical JSON representation. Mapping documented in new file `docs/object_json_schema.md` (created as part of implementation).

### Validation pipeline (all modes)

```
parse payload → resolve target → load current object →
apply operations → schema validate → KB ref validate →
emit XML → (if not dryRun) commit
```

Failure at any stage returns structured error with `path` pointing into the offending op/patch entry.

---

## 3. Dry-run uniforme

### Affected tools

`genexus_edit`, `genexus_create_object`, `genexus_refactor`, `genexus_forge`, `genexus_import_object`.

### Argument

`dryRun: boolean` (default `false`).

### Plan response schema (when `dryRun: true`)

```json
{
  "meta": {
    "dryRun": true,
    "tool": "genexus_edit",
    "schemaVersion": "mcp-axi/2"
  },
  "plan": {
    "touchedObjects": [
      { "type": "Transaction", "name": "Customer", "op": "modify" }
    ],
    "xmlDiff": "--- before\n+++ after\n@@ ...",
    "brokenRefs": [
      { "from": "Proc1", "fromType": "Procedure",
        "to": "Customer.Phone", "reason": "attribute_removed" }
    ],
    "warnings": [
      { "code": "W001", "message": "Rule references removed attribute", "path": "ops[3]" }
    ],
    "estimatedDurationMs": 120
  }
}
```

`xmlDiff` is unified-diff format. Truncated by default; `--full` flag (CLI) or `dryRun: { full: true }` (MCP) expands.

### Implementation

- Worker executes apply in a rollback-only transaction over an in-memory snapshot of the target object(s).
- `KbValidationService` (existing) runs the impact pass.
- Optional skip flag: `dryRun: { skipImpactAnalysis: true }` returns plan without `brokenRefs` (faster).

### Safety guarantees

- `dryRun: true` MUST NOT mutate KB state, file system, or build artifacts.
- `dryRun: true` MUST NOT consume idempotency cache slots.
- `dryRun: true` MUST NOT emit telemetry as a real edit.

---

## 4. Idempotency keys

### Argument

`idempotencyKey: string` (optional, on every write tool).
- Charset: `[A-Za-z0-9_-]`
- Length: 1..128
- Validation rejects out-of-spec keys with `usage_error`.

### Cache (Gateway, in-memory)

- Type: `ConcurrentDictionary<(kbPath, toolName, key), CachedEntry>`
- TTL: 15 minutes sliding (refreshes on hit). Configurable via `Server.IdempotencyTtlMinutes`.
- Capacity: 1000 entries per KB. LRU eviction. Configurable via `Server.IdempotencyCacheSize`.
- Composite key includes `kbPath` to prevent cross-KB collisions.

### Cached entry

```csharp
record CachedEntry(
  string PayloadHash,         // SHA256 of canonicalized request args
  JsonNode Result,            // full successful result
  DateTime CreatedAt,
  DateTime LastAccessedAt
);
```

### Lifecycle

1. Request arrives with `idempotencyKey`.
2. Lookup `(kbPath, tool, key)`:
   - **Hit, payload hash matches** → return cached result with `meta.idempotent: true`.
   - **Hit, payload hash differs** → `usage_error` code `idempotency_conflict`, message identifies divergent fields by path.
   - **Hit, in-flight** → second caller awaits first via per-key `SemaphoreSlim`, both receive same result.
   - **Miss** → execute; on success cache result; on error do not cache (allow retry).
3. `dryRun: true` skips cache entirely (read and write).

### Failure not cached

Errors are intentionally not cached so transient failures (worker crash, KB lock) can be retried with the same key safely. The first successful response becomes the cached result.

### Configuration surface

```json
{
  "Server": {
    "IdempotencyTtlMinutes": 15,
    "IdempotencyCacheSize": 1000
  }
}
```

---

## Schema version & meta

Bump `meta.schemaVersion` from `mcp-axi/1` → `mcp-axi/2`. Handshake `initialize` advertises new version. Response objects include:

- `meta.schemaVersion` (always)
- `meta.tool` (always)
- `meta.batched` (when `targets[]` used)
- `meta.dryRun` (when applicable)
- `meta.idempotent` (when cache hit)
- `meta.removedTools` (handshake only)

---

## Components touched

- `src/GxMcp.Gateway/` — handshake meta, idempotency cache, dispatcher consolidation, dryRun pass-through
- `src/GxMcp.Worker/` — semantic op handlers, JSON-Patch handler, dryRun rollback path, plan response shape, broken-ref impact analysis
- `src/GxMcp.Worker/KbValidationService` — extended for impact analysis
- `src/GxMcp.Worker.Tests/` — op catalog tests, dryRun safety tests, idempotency cache tests, removed-tool error tests
- `src/GxMcp.Gateway.Tests/` — handshake `removedTools` advertisement, idempotency key validation, in-flight semaphore behavior
- `docs/object_json_schema.md` — **new**, canonical JSON↔XML mapping
- `CHANGELOG.md` — Breaking Changes section
- `README.md` — tool surface list, new args
- `package.json` — version bump to `2.0.0`

## Testing strategy

- **Unit:** every semantic op (positive + negative cases), idempotency cache hit/miss/conflict/in-flight, dryRun rollback isolation.
- **Integration:** handshake advertises `removedTools`; calling removed tool returns `-32601`; dryRun produces plan without mutation; idempotency key returns identical bytes on retry.
- **Contract:** `mcp-axi/2` schema in `meta.schemaVersion` field; `meta.batched` only when `targets[]`; `meta.idempotent` only on cache hit.

## Risks

- **Op catalog completeness:** initial set may miss edge cases. Mitigation: `patch` mode is the explicit fallback for anything ops can't express.
- **Impact analysis cost:** broken-ref detection over large KBs may be slow. Mitigation: `skipImpactAnalysis` flag.
- **Cache memory:** 1000 × N KBs × result size. LRU + sliding TTL bound this; size configurable.
- **Breaking change blast:** existing v1.x consumers calling `batch_*` break hard. Mitigation: clear `-32601` with `replacedBy` data, prominent CHANGELOG, README migration note.

## Open questions

None — all clarifications resolved 2026-04-29.
