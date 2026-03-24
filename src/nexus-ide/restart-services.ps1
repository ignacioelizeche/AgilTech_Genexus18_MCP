# F5 pre-launch must not spawn a parallel gateway process.
# The extension BackendManager owns startup and reuse logic.
try {
    taskkill /F /IM GxMcp.Gateway.exe /T 2>$null
    taskkill /F /IM GxMcp.Worker.exe /T 2>$null

    $gatewayDotnet = Get-CimInstance Win32_Process |
        Where-Object {
            $_.Name -ieq 'dotnet.exe' -and
            $_.CommandLine -match 'GxMcp\.Gateway\.dll'
        }

    foreach ($proc in $gatewayDotnet) {
        try {
            Stop-Process -Id $proc.ProcessId -Force -ErrorAction Stop
            Write-Host "Stopped dotnet-hosted gateway PID $($proc.ProcessId)."
        } catch {}
    }

    $gatewayWrappers = Get-CimInstance Win32_Process |
        Where-Object {
            $_.Name -ieq 'powershell.exe' -and
            $_.CommandLine -match 'debug-gateway-wrapper\.ps1'
        }

    foreach ($proc in $gatewayWrappers) {
        try {
            Stop-Process -Id $proc.ProcessId -Force -ErrorAction Stop
            Write-Host "Stopped debug gateway wrapper PID $($proc.ProcessId)."
        } catch {}
    }

    Write-Host "Existing GeneXus MCP processes cleaned up."
} catch {
    Write-Host "No GeneXus MCP processes were running."
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\\..")
Get-ChildItem -Path $repoRoot -Force -Recurse -Filter ".mcp_config.json" -ErrorAction SilentlyContinue |
    ForEach-Object {
        try {
            Remove-Item $_.FullName -Force -ErrorAction Stop
            Write-Host "Removed stale discovery file $($_.FullName)."
        } catch {}
    }

Start-Sleep -Seconds 1
