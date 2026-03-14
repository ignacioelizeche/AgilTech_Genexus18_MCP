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
- local Knowledge Base configured in the gateway `config.json`

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
