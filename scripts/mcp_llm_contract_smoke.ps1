param(
    [string]$Root = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Split-Path -Parent $PSScriptRoot
}

Write-Host "[LLM-SMOKE] root: $Root"

$toolDefsPath = Join-Path $Root "src\GxMcp.Gateway\tool_definitions.json"
$mcpRouterPath = Join-Path $Root "src\GxMcp.Gateway\McpRouter.cs"
$cliRunPath = Join-Path $Root "cli\run.js"

if (-not (Test-Path $toolDefsPath)) { throw "tool_definitions.json not found: $toolDefsPath" }
if (-not (Test-Path $mcpRouterPath)) { throw "McpRouter.cs not found: $mcpRouterPath" }
if (-not (Test-Path $cliRunPath)) { throw "cli/run.js not found: $cliRunPath" }

Write-Host "[LLM-SMOKE] validate tool definitions"
$defs = Get-Content $toolDefsPath -Raw | ConvertFrom-Json

$requiredTools = @(
    "genexus_query",
    "genexus_list_objects",
    "genexus_read",
    "genexus_edit",
    "genexus_lifecycle"
)

foreach ($toolName in $requiredTools) {
    $match = $defs | Where-Object { $_.name -eq $toolName }
    if (-not $match) { throw "Required tool missing in definitions: $toolName" }
    if ([string]::IsNullOrWhiteSpace($match.description)) { throw "Tool description missing for: $toolName" }
}

Write-Host "[LLM-SMOKE] validate MCP static discovery surface"
$routerText = Get-Content $mcpRouterPath -Raw
if ($routerText -notmatch "genexus://kb/llm-playbook") { throw "Missing MCP resource: genexus://kb/llm-playbook" }
if ($routerText -notmatch "gx_bootstrap_llm") { throw "Missing MCP prompt: gx_bootstrap_llm" }

Write-Host "[LLM-SMOKE] validate CLI llm help command"
$cliOutput = node $cliRunPath llm help --format json
$parsed = $cliOutput | ConvertFrom-Json
if (-not $parsed.ok) { throw "llm help did not return ok payload." }
if (-not $parsed.ok.resources) { throw "llm help missing resources section." }
if (-not ($parsed.ok.resources -contains "genexus://kb/llm-playbook")) {
    throw "llm help resources does not include genexus://kb/llm-playbook."
}

Write-Host "[LLM-SMOKE] PASS"
