# GeneXus MCP Capabilities Inventory

This document records the MCP-facing surface that is currently exposed by the repository.

Status values:
- `active`: implemented and reachable through the current gateway-worker path
- `partial`: implemented but still limited in scope or ergonomics

## Transport

| Capability | Status | Notes |
| --- | --- | --- |
| stdio MCP loop | active | Main local transport for agent clients |
| `/mcp` HTTP endpoint | active | Supports POST, GET (SSE), and DELETE with MCP session headers |
| local bind default | active | Defaults to `127.0.0.1` through config |
| origin validation | partial | Loopback safe by default, configurable allowlist supported |
| session expiration | active | Idle sessions are removed automatically |

## Tools

Source of truth:
- `src/GxMcp.Gateway/tool_definitions.json`

| Tool | Status | Worker path |
| --- | --- | --- |
| `genexus_query` | active | `Search -> Query` |
| `genexus_read` | active | `Read -> ExtractSource` |
| `genexus_batch_read` | active | `Batch -> BatchRead` |
| `genexus_edit` | active | `Write` or `Patch -> Apply` via router conversion |
| `genexus_batch_edit` | active | `Batch -> MultiEdit` |
| `genexus_inspect` | active | `Analyze -> GetConversionContext` |
| `genexus_analyze` | active | `Analyze`, `Linter`, or `UI` depending on mode |
| `genexus_summarize` | active | `Analyze -> Summarize` |
| `genexus_inject_context` | active | `Analyze -> InjectContext` |
| `genexus_lifecycle` | active | `Build`, `KB`, or `Validation` depending on action |
| `genexus_forge` | partial | `Forge`, `Conversion`, and `Pattern` are now routed, but generation quality is still basic |
| `genexus_test` | active | `Test -> Run` |
| `genexus_get_sql` | active | `Analyze -> GetSQL` |
| `genexus_create_object` | active | `Object -> Create` |
| `genexus_refactor` | active | `Refactor -> RenameAttribute | RenameVariable | RenameObject | ExtractProcedure` |
| `genexus_format` | active | `Formatting -> Format` |
| `genexus_properties` | active | `Property -> Get | Set` |
| `genexus_history` | active | `History -> List | Get_Source | Save | Restore` |
| `genexus_structure` | active | `Structure -> GetVisualStructure | UpdateVisualStructure | GetVisualIndexes | GetLogicStructure` |
| `genexus_doc` | active | `Wiki`, `Visualizer`, or `Health` depending on action |

## Resources

| Resource or template | Status | Notes |
| --- | --- | --- |
| `genexus://kb/index-status` | active | KB indexing status |
| `genexus://kb/health` | active | Gateway and worker health report |
| `genexus://objects` | active | Browsable index of objects |
| `genexus://attributes` | active | Browsable attribute listing |
| `genexus://objects/{name}/part/{part}` | active | Part-specific object reading |
| `genexus://objects/{name}/variables` | active | Object variable declarations |
| `genexus://objects/{name}/navigation` | active | Navigation analysis |
| `genexus://objects/{name}/hierarchy` | active | Dependency hierarchy |
| `genexus://objects/{name}/data-context` | active | Data context bundle |
| `genexus://objects/{name}/ui-context` | active | UI context bundle |
| `genexus://objects/{name}/conversion-context` | active | Conversion-oriented context |
| `genexus://objects/{name}/pattern-metadata` | active | Pattern metadata |
| `genexus://objects/{name}/summary` | active | LLM-oriented summary |
| `genexus://objects/{name}/indexes` | active | Visual indexes for Transaction/Table objects |
| `genexus://objects/{name}/logic-structure` | active | Logical structure for Transaction/Table objects |
| `genexus://attributes/{name}` | active | Attribute metadata |
| resource subscriptions | partial | Subscription capability is advertised and notifications are emitted through the SSE session stream |

## Prompts

| Prompt | Status | Notes |
| --- | --- | --- |
| `gx_explain_object` | active | Grounded explanation workflow using source, variables, navigation, and summary |
| `gx_convert_object` | active | Conversion workflow with review gates and target-language argument |
| `gx_review_transaction` | active | Transaction review workflow focused on structure, rules, and risks |
| `gx_refactor_procedure` | active | Procedure refactor workflow focused on preserving behavior |
| `gx_generate_tests` | active | Test-plan generation workflow |
| `gx_trace_dependencies` | active | Dependency tracing workflow with impact analysis |

## Completion

| Capability | Status | Notes |
| --- | --- | --- |
| `completion/complete` | active | Supports structured completions for object parts, include fields, and target languages |

## Notifications

| Capability | Status | Notes |
| --- | --- | --- |
| `notifications/initialized` | active | Handled as a no-op |
| tools list changed notification | active | Emitted through the HTTP SSE session stream |
| resources list changed notification | active | Emitted through the HTTP SSE session stream |
| resource updated notification | active | Emitted through the HTTP SSE session stream |

## Extension integration

| Capability | Status | Notes |
| --- | --- | --- |
| local discovery file `.mcp_config.json` | active | Points to `/mcp` |
| default extension HTTP client | active | Extension runtime speaks MCP directly for discovery, VFS, providers, shadow sync, commands, and webviews |
| dynamic tool discovery in extension | active | Runtime discovery now loads tools, resources, and prompts from `/mcp` and caches the snapshot locally |
| MCP discovery commands in extension | active | Command Palette can inspect discovery, open resources, and run prompts from the cached snapshot |
| global Claude registration | active | Uses HTTP wrapper against `/mcp` |

## Known gaps

- Resource surface is still too small for rich object exploration.
- Prompt catalog is still minimal.
- Completions are currently static and schema-oriented; object-name completion is still pending.
- Prompt workflows are now available, but prompt arguments are still validated loosely in the gateway.
- Extension flows already migrated to MCP include discovery, prompts, resources, SQL, tests, build/rebuild, indexing, object creation, attribute rename, procedure extraction, properties, history, and structure/indexes views.
- `genexus_forge` is reachable now, but code generation quality is still early-stage.
