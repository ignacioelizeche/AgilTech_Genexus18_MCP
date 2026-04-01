@echo off
setlocal

:: Check for administrative privileges
net session >nul 2>&1
if %errorLevel% == 0 (
    echo [INFO] Running with administrative privileges.
) else (
    echo [WARN] Not running as administrator. Extension installation might require manual steps.
)

echo ==========================================
echo    GeneXus MCP One-Click Installer
echo ==========================================
echo.

:: Check for PowerShell
where powershell >nul 2>&1
if %errorLevel% neq 0 (
    echo [ERROR] PowerShell not found. Please install PowerShell and try again.
    pause
    exit /b 1
)

:: Run the script
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"

if %errorLevel% neq 0 (
    echo.
    echo [ERROR] Installation failed with error code %errorLevel%.
    pause
    exit /b %errorLevel%
)

echo.
echo [DONE] Installation process finished.
echo.
pause
