@echo off
REM Stop ClaudeMonitor process before uninstall
REM This ensures files are not locked when the MSI tries to remove them

tasklist /FI "IMAGENAME eq ClaudeMonitor.exe" 2>NUL | find /I "ClaudeMonitor.exe" >NUL
if %ERRORLEVEL% EQU 0 (
    taskkill /IM ClaudeMonitor.exe /F >NUL 2>&1
    timeout /t 2 /nobreak >NUL 2>&1
)

exit /b 0
