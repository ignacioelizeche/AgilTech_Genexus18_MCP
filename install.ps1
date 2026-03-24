# GeneXus 18 MCP Server installer

[CmdletBinding()]
param(
    [string]$KBPath,
    [string]$GeneXusPath,
    [switch]$SkipExtensionInstall,
    [switch]$SkipClaudeConfig,
    [switch]$SkipCodexConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$root = $PSScriptRoot
$configPath = Join-Path $root "config.json"
$publishDir = Join-Path $root "publish"
$extensionDir = Join-Path $root "src\nexus-ide"
$vsixPath = Join-Path $extensionDir "nexus-ide.vsix"
$startMcpBatPath = Join-Path $publishDir "start_mcp.bat"
$claudeConfigPath = Join-Path $env:APPDATA "Claude\claude_desktop_config.json"
$codexConfigPath = Join-Path $env:USERPROFILE ".codex\config.toml"

function Write-Step([string]$message) {
    Write-Host ""
    Write-Host $message -ForegroundColor Cyan
}

function Write-Ok([string]$message) {
    Write-Host $message -ForegroundColor Green
}

function Write-Warn([string]$message) {
    Write-Host $message -ForegroundColor Yellow
}

function Fail([string]$message) {
    Write-Host $message -ForegroundColor Red
    exit 1
}

function Backup-File([string]$path) {
    if (-not (Test-Path $path)) {
        return
    }

    $timestamp = Get-Date -Format "yyyyMMddHHmmss"
    $backupPath = "$path.$timestamp.bak"
    Copy-Item $path $backupPath -Force
}

function Get-ExistingPathOrPrompt([string]$label, [string]$currentValue) {
    if (-not [string]::IsNullOrWhiteSpace($currentValue) -and (Test-Path $currentValue)) {
        return $currentValue
    }

    while ($true) {
        $promptSuffix = if ([string]::IsNullOrWhiteSpace($currentValue)) { "" } else { " [$currentValue]" }
        $entered = Read-Host "$label$promptSuffix"
        if ([string]::IsNullOrWhiteSpace($entered)) {
            $entered = $currentValue
        }

        if (-not [string]::IsNullOrWhiteSpace($entered) -and (Test-Path $entered)) {
            return $entered
        }

        Write-Warn "Path not found. Please enter a valid path."
    }
}

function Save-JsonFile([string]$path, [object]$value) {
    $json = $value | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($path, $json, [System.Text.Encoding]::UTF8)
}

function Set-ClaudeConfig([string]$path, [string]$commandPath) {
    $configDir = Split-Path $path
    if (-not (Test-Path $configDir)) {
        New-Item -ItemType Directory -Path $configDir | Out-Null
    }

    if (Test-Path $path) {
        Backup-File $path
        $config = Get-Content $path -Raw | ConvertFrom-Json
    } else {
        $config = [pscustomobject]@{}
    }

    if (-not ($config.PSObject.Properties.Name -contains "mcpServers")) {
        $config | Add-Member -MemberType NoteProperty -Name "mcpServers" -Value ([pscustomobject]@{})
    }

    $existingServer = $config.mcpServers.PSObject.Properties["genexus18"]
    if ($existingServer) {
        $config.mcpServers.genexus18.command = $commandPath
        $config.mcpServers.genexus18.args = @()
    } else {
        $config.mcpServers | Add-Member -MemberType NoteProperty -Name "genexus18" -Value ([pscustomobject]@{
            command = $commandPath
            args = @()
        })
    }

    Save-JsonFile $path $config
}

function Set-CodexConfig([string]$path, [string]$url) {
    $configDir = Split-Path $path
    if (-not (Test-Path $configDir)) {
        New-Item -ItemType Directory -Path $configDir | Out-Null
    }

    if (Test-Path $path) {
        Backup-File $path
        $content = Get-Content $path -Raw
    } else {
        $content = ""
    }

    $sectionPattern = '(?ms)^\[mcp_servers\.genexus\]\s*.*?(?=^\[|\z)'
    $replacement = "[mcp_servers.genexus]`r`nurl = `"$url`"`r`n"

    if ($content -match $sectionPattern) {
        $updated = [System.Text.RegularExpressions.Regex]::Replace($content, $sectionPattern, $replacement)
    } else {
        $separator = if ([string]::IsNullOrWhiteSpace($content)) { "" } else { "`r`n`r`n" }
        $updated = $content.TrimEnd() + $separator + $replacement
    }

    [System.IO.File]::WriteAllText($path, $updated, [System.Text.Encoding]::UTF8)
}

function Resolve-CommandPath([string[]]$names) {
    foreach ($name in $names) {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($command) {
            return $command.Source
        }
    }

    return $null
}

function Invoke-NativeCommand([string]$commandPath, [string[]]$arguments) {
    & $commandPath @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: $commandPath $($arguments -join ' ')"
    }
}

function Get-EditorCommands() {
    $candidates = @(
        "code.cmd",
        "code",
        "code-insiders.cmd",
        "code-insiders",
        "cursor.cmd",
        "cursor",
        "codium.cmd",
        "codium",
        "antigravity.cmd",
        "antigravity"
    )
    $resolved = New-Object System.Collections.Generic.List[string]

    foreach ($candidate in $candidates) {
        $path = Resolve-CommandPath @($candidate)
        if ($path -and -not $resolved.Contains($path)) {
            $resolved.Add($path)
        }
    }

    return $resolved.ToArray()
}

Write-Host "Starting GeneXus MCP installation..." -ForegroundColor Green

if (-not (Test-Path $configPath)) {
    Fail "config.json not found at $configPath"
}

$config = Get-Content $configPath -Raw | ConvertFrom-Json

if ($PSBoundParameters.ContainsKey("GeneXusPath")) {
    $config.GeneXus.InstallationPath = $GeneXusPath
}
if ($PSBoundParameters.ContainsKey("KBPath")) {
    $config.Environment.KBPath = $KBPath
}

$config.GeneXus.InstallationPath = Get-ExistingPathOrPrompt "GeneXus installation path" $config.GeneXus.InstallationPath
$config.Environment.KBPath = Get-ExistingPathOrPrompt "Knowledge Base path" $config.Environment.KBPath

Backup-File $configPath
Save-JsonFile $configPath $config
Write-Ok "config.json updated."

$httpPort = 5000
if ($config.Server -and $config.Server.HttpPort) {
    try {
        $httpPort = [int]$config.Server.HttpPort
    } catch {}
}
$codexMcpUrl = "http://127.0.0.1:$httpPort/mcp"

Write-Step "[1/4] Building gateway, worker, and extension backend"
& (Join-Path $root "build.ps1")
if ($LASTEXITCODE -ne 0) {
    Fail "Build failed."
}
if (-not (Test-Path $startMcpBatPath)) {
    Fail "Build completed but $startMcpBatPath was not generated."
}
Write-Ok "Build completed."

if (-not $SkipExtensionInstall) {
    Write-Step "[2/4] Packaging and installing the VS Code extension"
    Push-Location $extensionDir
    try {
        $npmCommand = Resolve-CommandPath @("npm.cmd", "npm")
        $npxCommand = Resolve-CommandPath @("npx.cmd", "npx")
        if (-not $npmCommand) {
            throw "npm was not found in PATH."
        }
        if (-not $npxCommand) {
            throw "npx was not found in PATH."
        }

        if (Test-Path (Join-Path $extensionDir "package-lock.json")) {
            Invoke-NativeCommand $npmCommand @("ci", "--silent")
        } else {
            Invoke-NativeCommand $npmCommand @("install", "--silent")
        }

        Invoke-NativeCommand $npmCommand @("run", "compile")
        Invoke-NativeCommand $npxCommand @("--yes", "@vscode/vsce", "package", "--out", "nexus-ide.vsix")

        $editorCommands = Get-EditorCommands
        if ($editorCommands.Length -gt 0) {
            foreach ($editorCommand in $editorCommands) {
                Invoke-NativeCommand $editorCommand @("--install-extension", $vsixPath, "--force")
                Write-Ok "Extension installed via $editorCommand"
            }
        } else {
            Write-Warn "No supported editor CLI was found. Install $vsixPath manually."
        }
    } catch {
        Write-Warn "Automatic VS Code extension installation failed: $($_.Exception.Message)"
        Write-Warn "You can still install $vsixPath manually."
    } finally {
        Pop-Location
    }
} else {
    Write-Step "[2/4] Skipping VS Code extension installation"
}

if (-not $SkipClaudeConfig) {
    Write-Step "[3/4] Configuring Claude Desktop"
    try {
        Set-ClaudeConfig -path $claudeConfigPath -commandPath $startMcpBatPath
        Write-Ok "Claude Desktop configured at $claudeConfigPath"
    } catch {
        Write-Warn "Failed to update Claude Desktop config: $($_.Exception.Message)"
    }
} else {
    Write-Step "[3/4] Skipping Claude Desktop configuration"
}

if (-not $SkipCodexConfig) {
    Write-Step "[4/4] Configuring Codex Desktop"
    try {
        Set-CodexConfig -path $codexConfigPath -url $codexMcpUrl
        Write-Ok "Codex configured at $codexConfigPath"
    } catch {
        Write-Warn "Failed to update Codex config: $($_.Exception.Message)"
    }
} else {
    Write-Step "[4/4] Skipping Codex Desktop configuration"
}

Write-Host ""
Write-Ok "Installation complete."
Write-Host ""
Write-Host "Artifacts:" -ForegroundColor Cyan
Write-Host "  Backend launcher: $startMcpBatPath"
Write-Host "  VS Code extension: $vsixPath"
Write-Host ""
Write-Host "Cursor/Cline MCP snippet:" -ForegroundColor Cyan
Write-Host '{'
Write-Host '  "mcpServers": {'
Write-Host '    "genexus18": {'
Write-Host "      ""command"": ""$($startMcpBatPath -replace '\\', '\\')"","
Write-Host '      "args": []'
Write-Host '    }'
Write-Host '  }'
Write-Host '}'
Write-Host ""
Write-Host "If Claude or Codex was open, restart the app to pick up the new MCP configuration."
