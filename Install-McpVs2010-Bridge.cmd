@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Install-Vsix.ps1" %*
set "installerExitCode=%ERRORLEVEL%"
echo.
if not "%installerExitCode%"=="0" (
    echo VS2010 Bridge installation failed. Exit code: %installerExitCode%
) else (
    echo VS2010 Bridge installation completed.
)
pause
exit /b %installerExitCode%
