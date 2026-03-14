---
name: GeneXus MCP Mastery
description: Current MCP-first usage guide for the GeneXus gateway, worker, resources, prompts, and editing workflows.
---

# GeneXus MCP Mastery

Use this skill when interacting with the GeneXus KB through the MCP server in this repository.

## Transport baseline

- Official transports: stdio MCP and HTTP MCP at `/mcp`.
- HTTP clients must `initialize` first, send `MCP-Protocol-Version: 2025-06-18`, and persist the returned `MCP-Session-Id`.
- The gateway is MCP-only. Use `/mcp`.

## Primary tools

| Tool | Use |
| --- | --- |
| `genexus_query` | Find objects, references, signatures, and entry points. Supports optional `typeFilter` and `domainFilter` |
| `genexus_read` | Read one part with pagination |
| `genexus_batch_read` | Fetch coordinated context across several parts or objects |
| `genexus_edit` | Apply a focused edit or overwrite one part |
| `genexus_batch_edit` | Commit multiple edits atomically |
| `genexus_analyze` | Run navigation, lint, UI, or summary analysis |
| `genexus_inspect` | Get structured object or conversion context |
| `genexus_lifecycle` | Build, validate, reindex, and KB operations |
| `genexus_refactor` | Use supported rename or extraction flows |
| `genexus_format` | Format code through the worker formatter |
| `genexus_properties` | Read or update properties |
| `genexus_history` | Read or restore versions |
| `genexus_structure` | Read or update logical and visual structure |

## Preferred workflow

1. Discover capabilities with `tools/list`, `resources/list`, and `prompts/list`.
2. Search with `genexus_query`.
3. Read only the needed parts with `genexus_read` or `resources/read`.
4. If a change spans several files or parts, switch to `genexus_batch_edit`.
5. Validate with `genexus_lifecycle` or the specific build/test command that matches the task.

## Reading rules

- Prefer paginated `genexus_read` calls for large procedures and transactions.
- Use `genexus_batch_read` when the task requires source, rules, events, or variables together.
- Prefer resources for stable context panes and browsable metadata:
  - `genexus://objects/{name}/part/{part}`
  - `genexus://objects/{name}/variables`
  - `genexus://objects/{name}/navigation`
  - `genexus://objects/{name}/indexes`
  - `genexus://objects/{name}/logic-structure`
  - `genexus://attributes/{name}`

## Editing rules

- Use `genexus_edit` for a single object part.
- Use `genexus_batch_edit` for atomic multi-object work.
- Use `genexus_add_variable` when the intent is explicit variable creation instead of free-form source mutation.
- Use `genexus_format` after material source changes when formatting matters.
- Do not rely on retired names such as `genexus_patch`, `genexus_read_source`, or `genexus_write_object`.

## Refactor and structure rules

- For renames and extraction flows, prefer `genexus_refactor` over manual text edits.
- For transactions and visual designers, use `genexus_structure` instead of custom editor payloads.
- For metadata changes, use `genexus_properties`.

## Anti-patterns

- Do not hardcode tool availability. Discovery is live.
- Do not treat hidden transport adapters as the contract.
- Do not fetch full object bodies when a paginated or resource-based read is enough.
