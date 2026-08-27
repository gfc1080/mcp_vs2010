@echo off
setlocal

if not exist "%~dp0Install-McpVs2010-Bridge.ps1" goto missing_script
if not exist "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" goto missing_powershell

echo Starting MCP VS2010 Bridge installer...
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-McpVs2010-Bridge.ps1"
set "exitCode=%ERRORLEVEL%"
if "%exitCode%"=="0" echo Installation completed.
if not "%exitCode%"=="0" echo Installation failed. Exit code: %exitCode%
pause
exit /b %exitCode%

:missing_script
echo Install-McpVs2010-Bridge.ps1 was not found.
exit /b 2

:missing_powershell
echo Windows PowerShell was not found.
exit /b 2
