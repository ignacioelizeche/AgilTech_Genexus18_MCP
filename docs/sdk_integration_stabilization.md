# Stabilizing Native GeneXus SDK Integration

This document summarizes the runtime decisions that keep the worker stable when loading and executing the GeneXus 18 SDK.

## Core constraints

- The worker must run as x86 because the GeneXus SDK depends on 32-bit components.
- SDK bootstrapping must follow the correct native initialization order.
- Assembly resolution must search the GeneXus installation paths instead of assuming local publish copies.
- KB lifecycle should be centralized so multiple services do not initialize the SDK inconsistently.

## Current MCP-facing outcome

The stable SDK foundation now backs the MCP contracts used by the repository, including:

- `genexus_query`
- `genexus_read`
- `genexus_edit`
- `genexus_doc`
- `genexus_properties`
- `genexus_history`
- `genexus_structure`

## Documentation note

Older document names such as `genexus_list_objects`, `genexus_read_object`, and `genexus_write_object` refer to pre-MCP or early-wrapper phases and should not be used as current contract names.
