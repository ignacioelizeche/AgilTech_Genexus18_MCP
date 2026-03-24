# Nexus IDE for GeneXus

![Nexus IDE for GeneXus](resources/extension-icon.png)

Nexus IDE is a GeneXus-focused extension for VS Code-family editors built directly on top of the repository MCP server.

## What It Does

- mounts the active Knowledge Base as a virtual workspace
- browses objects through a GeneXus explorer
- opens and edits Source, Rules, Events, Variables, Structure, Layout, and Indexes
- reads MCP tools, resources, prompts, and completions directly from the gateway
- supports build, SQL inspection, history, properties, refactor, formatting, and test workflows

## Runtime Model

The extension talks to the local GeneXus MCP gateway at `/mcp`.

Core flow:

1. start or reuse the local gateway
2. initialize an MCP session
3. discover tools/resources/prompts
4. drive the virtual filesystem and editor providers through MCP

## Quick Start

1. Run `.\install.ps1` from the repository root.
2. Restart your editor if it was already open.
3. Open the command palette and run `GeneXus: Open KB`.

If the editor CLI is not available in `PATH`, install the generated VSIX manually.

## Development Debug

`F5` runs `Nexus IDE: Prepare Debug`, which:

1. cleans stale MCP processes
2. builds the worker, gateway, and extension
3. runs a short bootstrap task that starts a detached debug gateway wrapper on the same HTTP port defined in the repository root `config.json`
4. launches the extension host, which reuses that gateway instead of spawning a second backend inside the host

The bootstrap task only releases the debugger after the worker log reaches `Worker SDK ready.`.
The long-lived gateway is owned by `src/nexus-ide/debug-gateway-wrapper.ps1`, which is launched outside the VS Code task job so the backend survives the end of the prelaunch step.
If a ready gateway is already listening, the extension reuses it even when `genexus.autoStartBackend` is disabled.
If the backend drops during reindex, materialization, or file hydration, the extension recovers it by invoking the same `src/nexus-ide/start-debug-gateway.ps1` bootstrap path instead of using a separate in-host launcher.
Each recovery rotates `gateway_debug.log` and `worker_debug.log` into `.prev.log` instead of truncating them, so the previous crash trail is preserved.
`F5` now runs the repository `build.ps1` before launching, so the debug host, `publish/start_mcp.bat`, and the extension backend fallback all receive the same freshly built gateway/worker artifacts.

## Main Commands

- `GeneXus: Open KB`
- `GeneXus: Refresh Tree`
- `GeneXus: New Object`
- `GeneXus: Build with this only`
- `GeneXus: Get SQL Create Table (DDL)`
- `GeneXus: Show MCP Discovery Snapshot`
- `GeneXus: Open MCP Resource`
- `GeneXus: Run MCP Prompt`

## Requirements

- GeneXus 18
- .NET 8 SDK
- local Knowledge Base configured in the repository root [`config.json`](../../config.json)

## Configuration

The canonical runtime configuration is the repository root [`config.json`](../../config.json).

- `F5` debug and extension runtime pass `GX_CONFIG_PATH` to the gateway so debug, packaged backend, and local scripts read the same file
- the HTTP port defaults to `Server.HttpPort` from the canonical root `config.json`; `genexus.mcpPort` is only an explicit override
- stdio mode remains a runtime override (`GX_MCP_STDIO`), not an edit to copied build artifacts
- build output copies of `config.json` remain fallback artifacts only

## MCP Surface

The extension is MCP-first and expects the gateway to expose the GeneXus toolset, including:

- `genexus_query`
  - supports optional `typeFilter` and `domainFilter` for lighter server-side browse flows
- `genexus_read`
- `genexus_edit`
- `genexus_inspect`
- `genexus_analyze`
- `genexus_lifecycle`
- `genexus_refactor`
- `genexus_format`
- `genexus_properties`
- `genexus_history`
- `genexus_structure`

## Repository

Project repository: [Genexus18MCP](https://github.com/lennix1337/Genexus18MCP)
