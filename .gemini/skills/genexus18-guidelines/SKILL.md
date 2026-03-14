---
name: GeneXus 18 Guidelines
description: GeneXus 18 engineering rules that stay valid when the KB is accessed through the MCP server.
---

# GeneXus 18 Development Guidelines

This skill defines the domain rules. Pair it with the MCP mastery skill for transport and tool usage.

## Security

- Prefer GAM for authentication and authorization.
- Keep sensitive configuration outside source control.
- Avoid direct SQL when a GeneXus navigation or Business Component can express the same behavior safely.

## Data and transactions

- Use Business Components for insert, update, and delete flows that must respect GeneXus rules and referential integrity.
- Avoid `Commit` inside loops.
- Keep transaction rules concise. Move non-trivial logic into procedures.

## Navigation and performance

- Constrain `For Each` with `Where` or `Defined By`.
- Page large grids and expensive reads.
- Validate navigation plans when changing transactions or heavy procedures.

## KB structure

- Keep modules organized. Do not accumulate unrelated objects in root.
- Use clear naming conventions for Procedures, Transactions, Web Panels, Data Providers, and SDTs.
- Prefer declarative GeneXus structures and supported refactors over raw text surgery.

## MCP-specific operating rules

- Use the MCP tools that match the object model instead of inventing ad hoc gateway commands.
- For metadata changes, use `genexus_properties`.
- For visual or logical designers, use `genexus_structure`.
- For supported renames and extraction flows, use `genexus_refactor`.
- For source edits, prefer `genexus_edit` or `genexus_batch_edit` and then validate.

## Anti-patterns

- No hardcoded URLs when a Location or environment setting should own the value.
- No blocking waits in interactive web flows.
- No direct dependence on hidden or deprecated transport contracts outside MCP.
