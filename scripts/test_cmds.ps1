$init = '{"jsonrpc": "2.0", "method": "initialize", "params": {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "test-client", "version": "1.0.0"}}, "id": 1}'
$call = '{"jsonrpc": "2.0", "method": "tools/call", "params": {"name": "genexus_query", "arguments": {"query": "Consulta", "limit": 5}}, "id": 2}'

Write-Output $init
Start-Sleep -Seconds 15
Write-Output $call
