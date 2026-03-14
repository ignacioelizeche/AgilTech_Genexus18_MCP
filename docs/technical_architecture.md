# GeneXus MCP Technical Architecture

This document describes the current runtime architecture. It is not a historical roadmap.

## Runtime model

The system uses a dual-process design:

- `GxMcp.Gateway` on .NET 8 owns the MCP protocol surface.
- `GxMcp.Worker` on .NET Framework 4.8 owns GeneXus SDK execution.

```mermaid
graph LR
    A[Client] -->|stdio or HTTP /mcp| B[Gateway]
    B -->|worker RPC| C[Worker]
    C -->|Artech SDK| D[Knowledge Base]
```

## Transport

### Official transports

- stdio MCP
- HTTP MCP at `/mcp`

### HTTP behavior

- `initialize` is required before tool or resource calls.
- Requests must use `MCP-Protocol-Version: 2025-06-18`.
- Session-aware HTTP uses `MCP-Session-Id`.
- `GET /mcp` is used for SSE notifications.
- `DELETE /mcp` closes the session.

## Discovery-first surface

Clients are expected to discover capabilities dynamically:

- `tools/list`
- `resources/list`
- `resources/templates/list`
- `prompts/list`
- `completion/complete`

The extension uses this MCP discovery flow directly.

## Worker responsibilities

- Open and manage the active KB
- Read and write object parts
- Execute analysis, refactor, formatting, lifecycle, history, structure, and property operations
- Isolate GeneXus runtime constraints from the gateway process

## Gateway responsibilities

- MCP routing
- HTTP session lifecycle
- Worker lifecycle and restart boundaries
- Dynamic tool publication from `tool_definitions.json`
- Resource, prompt, and completion exposure

## Design constraints

- New features should target MCP tools, resources, prompts, or completion endpoints.
- New extension flows must only target MCP contracts.
- Resource reads should be preferred for stable browsable context.
- Large object reads should be paginated with `genexus_read` or coordinated with `genexus_batch_read`.
