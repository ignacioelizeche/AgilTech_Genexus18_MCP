# MCP Limitations Tracking

This artifact tracks the current MCP limitations discovered during real KB usage and the concrete work needed to remove them.

It is intended to be updated only after each item is validated in practice, not just implemented.

## Status legend

- `TODO`: not started
- `IN_PROGRESS`: implementation underway
- `VALIDATING`: implemented, waiting for practical confirmation
- `DONE`: confirmed with real MCP usage
- `BLOCKED`: cannot proceed until a dependency or SDK limitation is resolved

## Current findings baseline

Validated on `2026-03-25`:

- `genexus_read` now treats empty parts as valid responses instead of errors.
- HTTP MCP session flow works with `initialize`, `tools/list`, and `tools/call`.
- `notifications/initialized` now returns `204` instead of `400`.
- `genexus_asset` is available through MCP for KB binary assets and templates.
- `ControleExtensaoHorasLoad` business logic was updated and confirmed by MCP re-read.
- `RelControleExtensaoHoras` report source was updated and confirmed by MCP re-read.
- Visual metadata changes for the `ControleExtensaoHoras` WorkWithPlus grid are still not persisting reliably through the current MCP editing path.

## Workstreams

### 1. Binary assets and report templates

Goal:
allow MCP clients to read and write binary assets stored in the KB, especially `.xlsx` report templates.

Status: `VALIDATING`

Scope:

- read template assets from KB storage
- write updated template assets back to KB storage
- expose metadata: file name, MIME type, size, hash
- support safe base64 transport through MCP

Validation:

- read `RelControleExtensaoHoras.xlsx` through MCP
- update a header cell through MCP
- re-read the same asset and confirm hash/content change

Validated so far:

- `tools/list` exposes `genexus_asset`
- `find` locates `.xlsx` assets inside the active KB with `relativePath`, `mimeType`, `size`, and `sha256`
- `read` is now metadata-first by default and does not inject Base64 unless `includeContent=true`
- small `.xlsx` assets roundtrip through `read(includeContent=true)` and `write` with matching hash after re-read
- large generated report files return metadata successfully and reject `includeContent=true` when they exceed `maxBytes`, avoiding broken truncated payloads

Remaining validation before `DONE`:

- identify the authoritative `RelControleExtensaoHoras` template file inside the KB
- update a real header cell in that template through MCP and confirm the persisted change

### 2. WebForm and grid metadata editing

Goal:
edit captions, visibility, and column metadata for WebPanel grids and WorkWithPlus-generated controls without relying on fragile XML patching.

Status: `IN_PROGRESS`

Scope:

- expose raw editable `WebForm` metadata
- expose control tree introspection for UI controls
- support updates to grid columns by control identity
- support title and visibility updates for columns

Validation:

- rename `Débito` to `Débito (horas devidas)` in `ControleExtensaoHoras`
- expose the `Horas contabilizadas` column
- re-read the control metadata and confirm the changes
- confirm the grid renders with the expected captions

Validated so far:

- `genexus_read(part='Layout')` now returns editable `GxMultiForm` XML instead of preview HTML
- `genexus_read(part='PatternInstance')` on `ControleExtensaoHoras` now resolves to `WorkWithPlusControleExtensaoHoras`
- the resolved `PatternInstance` returns the real WorkWithPlus XML instance instead of the previous `<Properties />` stub
- the gateway now preserves `PatternInstance` XML without injecting truncation markers into the editable payload
- `genexus_edit(part='PatternInstance')` now completes over HTTP for a no-op roundtrip on `ControleExtensaoHoras`

Open blocker:

- a real business mutation of the WorkWithPlus grid columns is still pending validation against the authoritative `PatternInstance`

### 3. Verified persistence after writes

Goal:
stop returning false-positive `Success` when the SDK save path does not actually change the KB artifact.

Status: `IN_PROGRESS`

Scope:

- re-read after each write
- compare expected vs persisted content
- return explicit persistence error when the post-save state does not match

Validation:

- force a known UI metadata change
- verify MCP returns success only when re-read confirms the exact update

### 4. Source-read budget discipline

Goal:
keep source reads usable for models without flooding context or mixing unrelated metadata.

Status: `DONE`

Validated:

- `genexus_read` defaults to a source-first first page for MCP clients
- derived metadata is no longer auto-attached in MCP reads
- gateway trims oversized read payloads more aggressively

### 5. Empty part semantics

Goal:
treat `Rules`, `Conditions`, and similar empty parts as valid empty content.

Status: `DONE`

Validated:

- `ControleExtensaoHoras` `Conditions` returned `200`
- `source` returned as empty string
- `isEmpty = true`
- no error envelope was produced

### 6. Real MCP smoke tests

Goal:
have a repeatable smoke test covering session flow and critical KB artifact operations.

Status: `TODO`

Scope:

- `initialize`
- `tools/list`
- `resources/read`
- `genexus_read` with empty part
- `genexus_read` with paginated source
- asset read/write once binary support exists
- UI metadata read/write once WebForm support exists

Validation:

- one command or script runs the checks and emits pass/fail output

### 7. Pattern-awareness diagnostics

Goal:
identify whether a visible UI element comes from base object metadata, generated WebForm metadata, or WorkWithPlus pattern projection.

Status: `TODO`

Scope:

- expose origin metadata for controls and columns
- detect when a visual change must be applied to pattern-owned metadata rather than source or layout

Validation:

- for `ControleExtensaoHoras`, identify the authoritative source of the grid column captions

Validated so far:

- the `ControleExtensaoHoras` grid captions are not authoritative in `Layout`
- the authoritative metadata is in the `PatternInstance` of `WorkWithPlusControleExtensaoHoras`

## Immediate execution order

1. Binary assets and report templates
2. WebForm and grid metadata editing
3. Verified persistence after writes
4. Real MCP smoke tests
5. Pattern-awareness diagnostics

## Update rule

When changing this file:

- move an item to `DONE` only after MCP re-read or runtime confirmation
- record blockers explicitly instead of leaving silent failure
- prefer concrete validation notes over generic progress statements
