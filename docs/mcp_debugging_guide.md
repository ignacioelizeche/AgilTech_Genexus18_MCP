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

## What changed from the old model

- HTTP MCP is active and official.
- The gateway HTTP surface is `/mcp` only.
- Nexus-IDE and current clients should be debugged through the MCP session flow.
