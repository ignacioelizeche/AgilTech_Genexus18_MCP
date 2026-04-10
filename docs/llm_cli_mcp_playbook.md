# LLM Playbook: Best Use of AXI CLI + MCP

This guide is the practical reference for agents using `genexus-mcp` through shell (AXI CLI) and MCP (`tools/call`).

## Objective

- Minimize tokens and retries.
- Keep responses deterministic and machine-parsable.
- Use the right interface for each job.

## Interface Selection

Use AXI CLI when:
- You need environment/bootstrap checks (`home`, `status`, `doctor`).
- You need local installer/config introspection (`config show`, `tools list`).
- You want predictable shell-native exit codes (`0/1/2`) and strict `--fields`.

Use MCP when:
- You need KB operations (`genexus_query`, `genexus_read`, `genexus_edit`, etc.).
- You need long-running operation tracking (`genexus_lifecycle` + `op:<operationId>`).
- You are inside an MCP-native client loop.

## AXI CLI Contract (LLM-facing)

Entry points:
- `genexus-mcp home` or `genexus-mcp axi home`
- `genexus-mcp llm help`
- `genexus-mcp status`
- `genexus-mcp doctor --mcp-smoke`
- `genexus-mcp tools list`
- `genexus-mcp config show`

Rules:
- Parse `stdout` only.
- Expect envelope fields: `ok`, `error`, `help`, `meta`.
- Expect `meta.schemaVersion=axi-cli/1` and `meta.command`.
- Usage errors return `error.code=usage_error` and exit code `2`.
- Operational errors return exit code `1`.

Recommended flags:
- `--format json` for strict parsers.
- `--fields` to reduce payload size.
- `--full` only when needed.
- `--quiet` in automation contexts.

## MCP Contract (LLM-facing)

For `tools/call`, parse `result.content[0].text` as JSON.

Gateway AXI-like enrichments are additive:
- `meta.schemaVersion = mcp-axi/1`
- `meta.tool = <tool-name>`
- Collection helpers when inferable: `returned`, `total`, `empty`, `hasMore`, `nextOffset`
- Truncation signal: `meta.truncated=true` + contextual `help`
- Idempotent writes may include `noChange=true`

Optional shaping for `genexus_query` and `genexus_list_objects`:
- `fields`: array or CSV projection.
- `axiCompact=true`: compact default projection.

## High-Value Query Patterns

Discovery:
- `genexus_query(query='@quick', limit=20)`
- `genexus_list_objects(parentPath='Module/Folder', limit=200, offset=0)`

Disambiguation:
- Prefer `parentPath` over `parent` when folder names repeat.

Efficient reads:
- `genexus_read(name='Obj', part='Source', offset=1, limit=200)`
- For many files, prefer `genexus_batch_read`.

Safe edits:
- Use `genexus_edit(mode='patch', operation='Replace', context=..., content=..., expectedCount=1, dryRun=true)` before applying.
- Use `genexus_batch_edit` for multi-object coordinated changes.

## Timeout and Long-Running Operations

If a tool exceeds gateway budget, the response is still machine-actionable:
- `result.isError=true`
- payload `status='Running'`
- `operationId` and `correlationId`
- `help` with explicit lifecycle follow-up

Follow-up flow:
1. Call `genexus_lifecycle(action='status', target='op:<operationId>')`.
2. When complete, call `genexus_lifecycle(action='result', target='op:<operationId>')`.

Do not treat timeout as hard failure when `operationId` is present.

## Pagination and Token Budgeting

Read/list defaults are intentionally broad for humans, but not for LLMs.

Always set:
- `limit` and `offset` for list/search.
- `offset` and `limit` for `genexus_read`.

Prefer:
- `fields` or `axiCompact=true` on list-heavy calls.
- Multiple narrow calls over one large payload.

## Error Handling Policy for Agents

AXI CLI:
- Branch by process exit code first.
- Then parse `error.code` and `error.message`.

MCP:
- If JSON-RPC `error` exists, treat as transport/protocol failure.
- If `result.isError=true`, treat as domain/tool failure or running operation.
- When payload includes `help`, follow the first actionable step.

## Minimal End-to-End Recipes

Bootstrap + health:
1. `genexus-mcp home --format json`
2. `genexus-mcp doctor --mcp-smoke --format json`

Find + inspect + patch:
1. `genexus_query(query='name', limit=20)`
2. `genexus_read(name='ObjectName', part='Source', offset=1, limit=200)`
3. `genexus_edit(... dryRun=true ...)`
4. `genexus_edit(... dryRun=false ...)`

Large list with deterministic follow-up:
1. `genexus_list_objects(parentPath='Root Module', limit=200, offset=0, axiCompact=true)`
2. If `hasMore=true`, call again with `offset=nextOffset`.
