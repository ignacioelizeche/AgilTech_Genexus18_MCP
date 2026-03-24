---
name: GeneXus MCP Mastery
description: Current MCP-first usage guide for the GeneXus gateway, with coordination for specialized skills.
---

# GeneXus MCP Mastery

Use this skill to interact with the GeneXus KB through the MCP server in this repository.

## Coordination with Specialized Skills

When working on complex GeneXus tasks, coordinate this "Transport" skill with "Knowledge" skills:

- **GeneXus 18 Guidelines**: Use for object-specific modeling (TRN, PRC, DP, Panel) and core logic rules.
- **Frontend Skills**: Use for modern UI creation, design systems (Mercury), and Chameleon components.
- **Nexa Skill**: Use for specialized agent workflows and multi-object execution plans.

## Preferred Workflow

1. **Search**: Use `genexus_query` to find entry points.
2. **Read**: Use `genexus_read` or `resources/read` to get source/metadata.
3. **Plan**: Apply rules from **GeneXus 18 Guidelines** or **Nexa** to design the solution.
4. **Edit**: Use `genexus_edit` or `genexus_batch_edit` for focused changes.
5. **UI/UX**: If the task involves screens, apply **Frontend Skills** for modern aesthetics.
6. **Validate**: Use `genexus_lifecycle` to build and verify.

## Tool Best Practices

| Tool | Tip |
| --- | --- |
| `genexus_query` | Narrow down by `typeFilter` for faster results. |
| `genexus_read` | Always check the `Variables` part if adding logic. |
| `genexus_edit` | Prefer `mode=patch` for surgical changes to avoid overwriting unrelated code. |
| `genexus_properties` | Essential for enabling "Business Component" or "Expose as Web Service". |

## Anti-patterns

- Do not attempt to guess object names; always query first.
- Do not use `genexus_edit` without reading the latest state of the object.
- Avoid large monolithic edits; prefer smaller, validable changes.
