# Native SDK Insights

These notes capture implementation constraints that still matter when the worker edits GeneXus objects through MCP.

## Object-part persistence

When a service edits a specific GeneXus part, the worker must persist the part and then persist the parent object using the SDK flow that matches that object type.

## Cache invalidation

The worker maintains caches for performance. After any successful MCP write path, the relevant cached object state must be invalidated.

Current write-oriented MCP contracts include:

- `genexus_edit`
- `genexus_batch_edit`
- `genexus_doc`
- `genexus_properties`
- `genexus_structure`

## Naming note

Historical names such as `genexus_write_object` appear in older notes and should be read as predecessors of the current MCP write contracts, not as the public tool surface used today.
