# GeneXus MCP - Build & Deploy Script
# ==========================================

Set-StrictMode -Version Latest
$ErrorActionPreference = "Continue"
$ProgressPreference = "SilentlyContinue"
$root = $PSScriptRoot
$publishDir = Join-Path $root "publish"
$gatewayProject = Join-Path $root "src\GxMcp.Gateway\GxMcp.Gateway.csproj"
$workerProject = Join-Path $root "src\GxMcp.Worker\GxMcp.Worker.csproj"

function Fail-Build([string]$message, [int]$exitCode = 1) {
    Write-Host "[build] $message" -ForegroundColor Red
    exit $exitCode
}

function Invoke-DotNet([string[]]$arguments, [string]$failureMessage) {
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        Fail-Build $failureMessage $LASTEXITCODE
    }
}

Write-Host "[build] Preparing build..." -ForegroundColor Cyan

# 0. Stop running processes
Write-Host "   > Stopping running processes..."
Stop-Process -Name GxMcp.Worker -ErrorAction SilentlyContinue
Stop-Process -Name GxMcp.Gateway -ErrorAction SilentlyContinue

# Verify prerequisites
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCommand) {
    Fail-Build ".NET SDK was not found in PATH. Install .NET 8 SDK before running build.ps1."
}

$dotnetVersion = dotnet --version
Write-Host "   > Found .NET SDK: $dotnetVersion" -ForegroundColor Gray

# 0.1 Restore Dependencies
Write-Host "   > Restoring Gateway dependencies..."
Invoke-DotNet @("restore", $gatewayProject) "Gateway restore failed."

Write-Host "   > Restoring Worker dependencies..."
Invoke-DotNet @("restore", $workerProject) "Worker restore failed."

# Resolve GeneXus Path
$gxPath = "C:\Program Files (x86)\GeneXus\GeneXus18"
if (Test-Path (Join-Path $root "config.json")) {
    $configData = Get-Content (Join-Path $root "config.json") -Raw | ConvertFrom-Json
    if ($configData.GeneXus -and $configData.GeneXus.InstallationPath) {
        $gxPath = $configData.GeneXus.InstallationPath
    }
}

if (-not (Test-Path $gatewayProject)) {
    Fail-Build "Gateway project file was not found at $gatewayProject."
}

if (-not (Test-Path $workerProject)) {
    Fail-Build "Worker project file was not found at $workerProject."
}

if (-not (Test-Path $gxPath)) {
    Fail-Build "GeneXus installation path was not found: $gxPath."
}

if (-not (Test-Path (Join-Path $gxPath "Definitions"))) {
    Fail-Build "GeneXus Definitions folder was not found under $gxPath."
}

# Also stop dotnet processes running the Gateway (since we use 'dotnet GxMcp.Gateway.dll')
Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" |
    Where-Object { $_.CommandLine -like "*GxMcp.Gateway.dll*" } |
    ForEach-Object {
        Write-Host "     - Stopping dotnet process ($($_.ProcessId))..."
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }

Start-Sleep -Seconds 1

$ErrorActionPreference = "Stop"

# 1. Clean Publish Directory
if (Test-Path $publishDir) {
    Write-Host "   > Cleaning publish directory (preserving logs)..."
    Get-ChildItem -Path "$publishDir\*" -Exclude "worker_log.txt", "mcp_debug.log", "*.log", "GxMcp.Worker.exe.config" |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
} else {
    New-Item -Path $publishDir -ItemType Directory | Out-Null
}

Write-Host "[build] Building solutions..." -ForegroundColor Cyan

# 2. Build Gateway (.NET 8)
Write-Host "   > Building Gateway (Release)..."
$tempGw = Join-Path $publishDir "temp_gw"
Invoke-DotNet @("publish", $gatewayProject, "-c", "Release", "--nologo", "-o", $tempGw) "Gateway publish failed."

if (Test-Path $tempGw) {
    Copy-Item "$tempGw\*" "$publishDir" -Force -Recurse
    Remove-Item $tempGw -Recurse -Force
}

Write-Host "   > Building Gateway (Debug)..."
Invoke-DotNet @("build", $gatewayProject, "-c", "Debug", "--nologo") "Gateway debug build failed."

# 3. Build Worker (.NET Framework 4.8)
Write-Host "   > Building Worker (Release)..."
Invoke-DotNet @("build", $workerProject, "-c", "Release", "--nologo", "-p:GX_PATH=$gxPath") "Worker build failed."

Write-Host "   > Building Worker (Debug)..."
Invoke-DotNet @("build", $workerProject, "-c", "Debug", "--nologo", "-p:GX_PATH=$gxPath") "Worker debug build failed."

# 4. Copy Worker Binaries to Publish
$workerPublishDir = Join-Path $publishDir "worker"
if (-not (Test-Path $workerPublishDir)) {
    New-Item -Path $workerPublishDir -ItemType Directory | Out-Null
}

$workerBinRelease = Join-Path $root "src\GxMcp.Worker\bin\Release"
if (-not (Test-Path $workerBinRelease)) {
    $workerBinRelease = Join-Path $root "src\GxMcp.Worker\bin\x86\Release"
}

if (Test-Path $workerBinRelease) {
    Write-Host "   > Deploying Release Worker binaries to $workerPublishDir..."
    Get-ChildItem -Path "$workerBinRelease\*" -Recurse | Copy-Item -Destination "$workerPublishDir" -Recurse -Force
}

# 4.1 Copy GeneXus Definitions (Crucial for SDK)
if (Test-Path "$gxPath\Definitions") {
    Write-Host "   > Copying GeneXus Definitions..."
    if (-not (Test-Path "$workerPublishDir\Definitions")) {
        Copy-Item "$gxPath\Definitions" -Destination "$workerPublishDir\Definitions" -Recurse -Force
    }
}

# 5. Sync config fallback artifact from canonical root config
if (Test-Path "$root\config.json") {
    Write-Host "   > Syncing canonical config.json to publish fallback artifact..."
    Copy-Item "$root\config.json" -Destination "$publishDir\config.json" -Force
} else {
    Write-Host "   > Creating default config.json..."
    $defaultConfig = @{
        GeneXus = @{
            InstallationPath = "C:\\Program Files (x86)\\GeneXus\\GeneXus18"
            WorkerExecutable = "$publishDir\\worker\\GxMcp.Worker.exe"
        }
        Server = @{
            HttpPort = 5000
            McpStdio = $true
        }
        Logging = @{
            Level = "Debug"
            Path = "logs"
        }
        Environment = @{
            KBPath = "C:\\KBs\\academicoLocal"
        }
    } | ConvertTo-Json -Depth 4
    Set-Content "$publishDir\config.json" $defaultConfig
}

# 6. Generate start_mcp.bat
Write-Host "   > Generating start_mcp.bat..."
$batContent = @"
@echo off
setlocal

set "REPO_ROOT=$root"
set "GX_CONFIG_PATH=$root\config.json"
set "GX_MCP_STDIO=true"

set "DEBUG_GATEWAY=%REPO_ROOT%\src\GxMcp.Gateway\bin\Debug\net8.0-windows\GxMcp.Gateway.exe"
set "RELEASE_GATEWAY=%REPO_ROOT%\src\GxMcp.Gateway\bin\Release\net8.0-windows\GxMcp.Gateway.exe"

if exist "%DEBUG_GATEWAY%" (
  "%DEBUG_GATEWAY%"
  exit /b %ERRORLEVEL%
)

if exist "%RELEASE_GATEWAY%" (
  "%RELEASE_GATEWAY%"
  exit /b %ERRORLEVEL%
)

cd /d "%~dp0"
dotnet GxMcp.Gateway.dll
"@
Set-Content -Path "$publishDir\start_mcp.bat" -Value $batContent -Encoding Ascii

Write-Host "`n[build] Build complete." -ForegroundColor Green
Write-Host "   - Output: $publishDir"
Write-Host "   - Worker: $publishDir\worker\GxMcp.Worker.exe"
Write-Host "   - Gateway: $publishDir\GxMcp.Gateway.exe"

# 7. Deploy to Extension Backend (for live development)
$extBackendDir = Join-Path $root "src\nexus-ide\backend"
Write-Host "`n[build] Deploying to extension backend: $extBackendDir" -ForegroundColor Cyan
if (-not (Test-Path $extBackendDir)) {
    New-Item -Path $extBackendDir -ItemType Directory | Out-Null
}
Copy-Item "$publishDir\*" -Destination "$extBackendDir" -Recurse -Force
