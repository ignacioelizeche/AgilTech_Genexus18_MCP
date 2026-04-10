# AXI CLI Contract (LLM-Facing)

This document defines the machine-facing contract for `genexus-mcp` AXI commands.

## Scope

- AXI contract applies to explicit AXI subcommands:
  - `status`
  - `doctor`
  - `tools list`
  - `config show`
  - `init`
  - `help`
- MCP JSON-RPC passthrough mode remains unchanged when no AXI subcommand is used.

## Output Envelope

All AXI responses are structured with the same top-level envelope:

- `ok` (object): success payload
- `error` (object): failure payload (`code`, `message`)
- `help` (array<string>): contextual next-step suggestions
- `meta` (object): diagnostics and protocol metadata

`meta.schemaVersion` is always present and currently equals `axi-cli/1`.

## Exit Codes

- `0`: success (including idempotent no-op mutations)
- `1`: operational error (runtime/config/environment issue)
- `2`: usage error (invalid/missing flags/arguments)

## Output Channels

- `stdout`: structured AXI payload only
- `stderr`: diagnostics/progress only (suppressible with `--quiet`)

LLMs should parse only `stdout`.

## Formats

Supported via `--format`:

- `toon` (default for AXI commands)
- `json`
- `text`

## Truncation Policy

Large fields are truncated by default when supported by the command. Output includes:

- explicit truncation marker in content
- full-size hint in metadata/content
- escape hatch in `help` via `--full`

Current commands with truncation behavior:

- `tools list` (long descriptions)
- `config show` (`raw` JSON block)

## Command Contracts

## `status`

Default schema:

- `ok.ready`
- `ok.configFound`
- `ok.gatewayExeFound`
- `ok.kbLooksValid`

With `--full`, includes detailed paths/origin (`configPath`, `gatewayExePath`, `configSource`, etc.).

## `doctor`

Default schema per check item (minimal):

- `id`
- `status` (`pass|warn|fail`)
- `detail`

Includes:

- `ok.summary` aggregate counts
- `ok.returned` / `ok.total`

With `--full`, includes runtime spawn probe.

## `tools list`

Default list schema per tool:

- `name`
- `status`
- `required`

Includes:

- `ok.returned` / `ok.total`
- `ok.empty` definitive empty state flag
- `meta.totalByCategory` aggregate counts

Supports `--query` for filtering.

## `config show`

Returns compact config-derived fields plus `raw` config content (possibly truncated unless `--full`).

## `init`

Non-interactive first:

- required: `--kb`, `--gx`
- optional: `--write-clients`

Interactive mode only with `--interactive`.

Idempotency:

- if resulting config is unchanged, returns success with `ok.noOp = true`.

## `help`

Global and per-subcommand help:

- `genexus-mcp help`
- `genexus-mcp <command> --help`

Includes `ok.bin` (resolved executable path, home collapsed to `~`).

## Canonical Examples

### JSON

```json
{
  "ok": {
    "ready": true,
    "configFound": true,
    "gatewayExeFound": true,
    "kbLooksValid": true
  },
  "help": [],
  "meta": {
    "schemaVersion": "axi-cli/1"
  }
}
```

### TOON

```text
help: []
meta:
  schemaVersion: axi-cli/1
ok:
  configFound: true
  gatewayExeFound: true
  kbLooksValid: true
  ready: true
```

### Text

```text
ok: {"ready":true,"configFound":true,"gatewayExeFound":true,"kbLooksValid":true}
meta: {"schemaVersion":"axi-cli/1"}
```

## Definitive Empty State Example

`genexus-mcp tools list --query non-existent --format json`:

- `ok.tools: []`
- `ok.returned: 0`
- `ok.total: 0`
- `ok.empty: true`
- `help` contains actionable follow-up

## Compatibility Note (Content-First Principle)

AXI principle #8 recommends "content first" for no-args execution. This CLI intentionally preserves MCP launcher compatibility:

- no args => MCP passthrough (required for existing MCP clients)
- AXI content-first entrypoint => `genexus-mcp status`

## Session Hooks (Ambient Context)

To avoid unwanted context bloat, automatic self-install hooks are not enabled in this release. Recommended explicit pattern:

- Codex/Claude session-start hook command:
  - `genexus-mcp status --format toon --quiet`

This keeps ambient context opt-in and deterministic.

## AXI Principles Compliance Checklist

1. Token-efficient output: Yes (`toon` default for AXI commands)
2. Minimal default schemas: Yes (3–4 fields on list defaults)
3. Content truncation: Yes (`tools list`, `config show` + `--full` hints)
4. Pre-computed aggregates: Yes (`doctor.summary`, `tools.totalByCategory`)
5. Definitive empty states: Yes (`tools list` explicit empty state)
6. Structured errors & exit codes: Yes (`0/1/2`, structured `error`)
7. Ambient context: Partially (documented opt-in; no auto-install)
8. Content first: Partially (kept MCP no-args compatibility; `status` is AXI home)
9. Contextual disclosure: Yes (`help` next steps in outputs)
10. Consistent help: Yes (`help` + per-command `--help` + `ok.bin`)
