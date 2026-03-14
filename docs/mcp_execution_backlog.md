# GeneXus MCP Execution Backlog

This backlog reflects the post-migration state of the repository.

## Completed foundation

The following phases are already complete:

- MCP-native transport at `/mcp`
- protocol-version and session-aware HTTP behavior
- MCP discovery surface for tools, resources, prompts, completion, and notifications
- Nexus-IDE migration to MCP-first runtime
- removal of the legacy `/api/command` transport

## Current backlog

The remaining work is not "migrate to MCP". The remaining work is hardening and expansion.

### Track 1: Harden MCP contracts

Goal:
make the MCP surface stricter and easier for external clients to consume safely.

Tasks:
- increase contract coverage for tools, resources, prompts, and completion
- tighten schema validation for prompt arguments and tool payloads
- add more protocol-level regression tests for HTTP sessions and notifications

Acceptance:
- protocol regressions are caught by tests before release

### Track 2: Expand editor and exploration resources

Goal:
make resources the default read surface for rich GeneXus exploration.

Tasks:
- expand object and attribute resources where the worker already has stable data
- improve resource templates so external clients can compose URIs without hidden conventions
- enrich summaries and context resources for conversion and review flows

Acceptance:
- common read-only exploration no longer requires ad hoc tool calls

### Track 3: Conversion pipeline maturity

Goal:
stabilize the conversion-oriented surface on top of MCP.

Tasks:
- define and harden the conversion bundle contract
- evolve GeneXus IR toward deterministic translation inputs
- improve target generators and review outputs

Acceptance:
- conversion workflows are structured, testable, and predictable

## Immediate next priorities

1. Add more MCP protocol contract tests around sessions, SSE, prompts, and completion.
2. Expand resource coverage for object exploration and conversion support.
3. Stabilize the conversion-oriented MCP surface.
