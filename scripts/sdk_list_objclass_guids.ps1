
$gxPath = $env:GX_PROGRAM_DIR
if (-not $gxPath) {
    $configPath = Join-Path $PSScriptRoot "..\src\nexus-ide\backend\config.json"
    if (Test-Path $configPath) {
        $config = Get-Content $configPath | ConvertFrom-Json
        $gxPath = $config.InstallationPath
    }
}
if (-not $gxPath) { throw "GX_PROGRAM_DIR not set and config.json not found." }

Add-Type -Path (Join-Path $gxPath "Artech.Architecture.Common.dll")
Add-Type -Path (Join-Path $gxPath "Artech.Genexus.Common.dll")

$ocType = [Artech.Genexus.Common.ObjClass]
Write-Host "--- GeneXus Object Type GUIDs (ObjClass) ---"
$ocType.GetFields([System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Static) | Where-Object { $_.FieldType -eq [System.Guid] } | Sort-Object Name | ForEach-Object {
    Write-Host "$($_.Name): $($_.GetValue($null))"
}
