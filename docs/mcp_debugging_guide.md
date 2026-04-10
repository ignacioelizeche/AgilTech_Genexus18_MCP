# GeneXus MCP Debugging Guide

This guide documents how to debug the current MCP-first runtime.

## Runtime shape

- Client or extension talks MCP to the gateway.
- Gateway talks to the worker.
- Worker talks to the GeneXus SDK and KB.

## Primary checks

### HTTP MCP sanity

Validate against `/mcp`.

Required baseline:

- `MCP-Protocol-Version: 2025-06-18`
- `initialize` before other MCP requests
- `MCP-Session-Id` reused after initialization

Typical flow:

1. `initialize`
2. `tools/list`
3. `resources/list`
4. `tools/call`

### stdio sanity

When launching the gateway as a stdio MCP server:

- stdout must remain reserved for protocol messages
- logs belong on stderr
- the process must stay idle without printing banner text

## Common failure modes

### Invalid JSON-RPC id handling

Preserve the original JSON type of `id` in responses. Converting a numeric `id` into a string breaks clients even when the payload looks correct.

### Session misuse

If `/mcp` is returning protocol errors after initialization, verify that the client is reusing the correct `MCP-Session-Id`.

### Protocol-version mismatch

If initialization fails, verify `MCP-Protocol-Version: 2025-06-18`.

### Worker startup failure

If discovery works but `tools/call` fails, inspect worker startup and GeneXus SDK loading. The gateway can initialize without a healthy worker, but execution calls cannot succeed.

### Long-running tool timeout with operation tracking

When a tool exceeds the gateway timeout budget, the request may continue in the worker.

The timeout error now includes:

- `operationId`
- `correlationId`

Use:

- `genexus_lifecycle(action='status', target='op:<operationId>')`
- `genexus_lifecycle(action='result', target='op:<operationId>')`

Automated smoke script:

- `powershell -ExecutionPolicy Bypass -File scripts/mcp_smoke.ps1`

You can also stream status via SSE (`GET /mcp`) and listen for `notifications/message` entries emitted by the gateway.

### Patch ambiguity and no-match diagnostics

For `genexus_edit(mode='patch')`, the worker now emits explicit patch statuses:

- `Applied`
- `NoChange`
- `NoMatch`
- `Ambiguous`
- `Error`

Prefer checking `patchStatus` and `details` before retrying with larger payload changes.

### AXI-style MCP tool payload metadata

`tools/call` responses may now include additive gateway metadata inside the JSON text payload:

- `meta.schemaVersion` (current: `mcp-axi/1`)
- `meta.tool`
- list helpers (`returned`, `total`, `empty`, `hasMore`, `nextOffset`) when inferable
- truncation signal (`meta.truncated=true`) and contextual `help` hints

If a client seems to "lose fields", check whether `fields` or `axiCompact` was passed in the tool arguments for `genexus_query` or `genexus_list_objects`.

### Field projection and compact mode

For list-heavy calls:

- `fields`: array or comma-separated list for explicit projection
- `axiCompact=true`: compact defaults without changing tool semantics

These options reduce token volume while preserving protocol compatibility.

### Save fallback diagnostics

When source-part saves use fallback strategy (`object_save_only`), this is surfaced in response metadata (`retryStrategy`) and gateway metrics.

Fetch aggregate metrics with:

- `genexus_lifecycle(action='status', target='gateway:metrics')`

## What changed from the old model

- HTTP MCP is active and official.
- The gateway HTTP surface is `/mcp` only.
- Nexus-IDE and current clients should be debugged through the MCP session flow.
