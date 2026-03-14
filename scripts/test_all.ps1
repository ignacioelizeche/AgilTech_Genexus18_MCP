# GeneXus MCP & Nexus IDE - Comprehensive Test Runner

$hadFailures = $false

Write-Host "--- [1/3] Compiling Project ---" -ForegroundColor Cyan
.\build.ps1
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed"; exit 1 }

Write-Host "`n--- [2/3] Running MCP Internal Unit Tests ---" -ForegroundColor Cyan
$gwProcess = Start-Process -FilePath "C:\Projetos\GenexusMCP\publish\GxMcp.Gateway.exe" -WindowStyle Hidden -PassThru
Write-Host "Waiting for Gateway & Worker to initialize (SDK load)..."
Start-Sleep -Seconds 15

try {
    $initializeBody = '{"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test-runner","version":"1.0.0"}}}'
    $initResponse = Invoke-WebRequest -UseBasicParsing -Method Post -Uri "http://127.0.0.1:5000/mcp" -Body $initializeBody -ContentType "application/json" -Headers @{ "MCP-Protocol-Version" = "2025-03-26" } -TimeoutSec 60
    $sessionId = $null
    if ($initResponse -and $initResponse.Headers) {
        $sessionId = $initResponse.Headers["MCP-Session-Id"]
    }
    if (-not $sessionId) { throw "MCP session not established." }

    $toolsBody = '{"jsonrpc":"2.0","id":"tools","method":"tools/list"}'
    $toolsResponse = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:5000/mcp" -Body $toolsBody -ContentType "application/json" -Headers @{ "MCP-Protocol-Version" = "2025-03-26"; "MCP-Session-Id" = $sessionId } -TimeoutSec 60
    if (-not $toolsResponse -or -not $toolsResponse.result -or -not $toolsResponse.result.tools) {
        throw "tools/list did not return a tool catalog."
    }
    $toolNames = @($toolsResponse.result.tools | ForEach-Object { $_.name })
    if (-not ($toolNames -contains "genexus_doc")) { throw "Expected tool genexus_doc not found in tools/list." }

    $healthBody = '{"jsonrpc":"2.0","id":"health","method":"tools/call","params":{"name":"genexus_doc","arguments":{"action":"health"}}}'
    $response = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:5000/mcp" -Body $healthBody -ContentType "application/json" -Headers @{ "MCP-Protocol-Version" = "2025-03-26"; "MCP-Session-Id" = $sessionId } -TimeoutSec 60

    if ($response.error) {
        Write-Host "Gateway Error: $($response.error.message)" -ForegroundColor Red
    } elseif (-not $response.result.content[0].text) {
        throw "Health tool returned no MCP text content."
    } else {
        Write-Host "MCP Gateway discovery and worker roundtrip: PASS" -ForegroundColor Green
        Write-Host "  tools/list exposed genexus_doc"
        Write-Host "  genexus_doc(action=health) returned content"
    }
} catch {
    Write-Host "Error connecting to Gateway for tests: $_" -ForegroundColor Red
    if ($_.ScriptStackTrace) {
        Write-Host $_.ScriptStackTrace -ForegroundColor DarkRed
    }
    $hadFailures = $true
} finally {
    Stop-Process -Id $gwProcess.Id -Force -ErrorAction SilentlyContinue
}

Write-Host "`n--- [3/3] Running Nexus IDE UI Tests ---" -ForegroundColor Cyan
cd src/nexus-ide
npm test
if ($LASTEXITCODE -ne 0) { $hadFailures = $true }
cd ../..

Write-Host "`n--- Test Cycle Complete ---" -ForegroundColor Cyan
if ($hadFailures) { exit 1 }
