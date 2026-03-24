@echo off
setlocal

for %%I in ("%~dp0..") do set "REPO_ROOT=%%~fI"
set "GX_CONFIG_PATH=%REPO_ROOT%\config.json"
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
