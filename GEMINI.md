# GeneXus MCP Protocol Guide

> Required repo skills before editing KB logic:
>
> 1. [GeneXus MCP Mastery](./.gemini/skills/genexus-mastery/SKILL.md)
> 2. [GeneXus 18 Guidelines](./.gemini/skills/genexus18-guidelines/SKILL.md)

This repository is MCP-first. The official transport is MCP over stdio or HTTP at `/mcp`. The old `/api/command` endpoint is no longer part of the gateway surface.

## Correct MCP flow

1. Initialize the session with `initialize`.
2. Discover the live surface with `tools/list`, `resources/list`, `resources/templates/list`, `prompts/list`, and `completion/complete` when needed.
3. Execute work with `tools/call`, `resources/read`, and `prompts/get`.
4. For HTTP MCP, send `MCP-Protocol-Version: 2025-06-18` and reuse the returned `MCP-Session-Id`.

## Recommended tool usage

- `genexus_query`: find objects, references, signatures, and dependency entry points. Supports optional `typeFilter` and `domainFilter` for server-side narrowing.
- `genexus_read`: read object parts with pagination. Prefer this over large bulk reads.
- `genexus_batch_read`: fetch multiple parts when a workflow needs coordinated context.
- `genexus_edit`: apply focused edits to a part or replace content through the MCP write path.
- `genexus_batch_edit`: update multiple objects atomically.
- `genexus_inspect`: get structured conversion or object context.
- `genexus_analyze`: navigation, lint, UI, and summary analysis modes.
- `genexus_lifecycle`: build, validate, index, and KB lifecycle operations.
- `genexus_get_sql`: extract DDL and SQL-oriented schema insights.
- `genexus_create_object`: create new KB objects.
- `genexus_refactor`: supported rename and extraction refactors.
- `genexus_add_variable`: add variables through the worker contract.
- `genexus_format`: format source through the worker formatter.
- `genexus_properties`: get or set object properties.
- `genexus_history`: list, read, save, and restore object history.
- `genexus_structure`: read or update logical and visual structure.
- `genexus_doc`: access documentation, health, and visualization flows.

## Resource-first patterns

Prefer resources when the data is naturally browsable or cacheable:

- `genexus://objects/{name}/part/{part}`
- `genexus://objects/{name}/variables`
- `genexus://objects/{name}/navigation`
- `genexus://objects/{name}/summary`
- `genexus://objects/{name}/indexes`
- `genexus://objects/{name}/logic-structure`
- `genexus://attributes/{name}`
- `genexus://kb/index-status`
- `genexus://kb/health`

## Operating rules

- Do not design new features around non-MCP transport contracts.
- Do not use retired tool names such as `genexus_patch`, `genexus_read_source`, or `genexus_write_object`.
- Read first, then edit. Use paginated reads for large objects.
- Prefer MCP discovery over hardcoded assumptions about available tools or resources.
- After C# changes, run `.\build.ps1`. For current validation commands, follow the README.
