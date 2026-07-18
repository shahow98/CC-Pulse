@echo off
REM CC-Pulse Hook Script (Windows)
REM This script sends Claude Code session status updates to the CC-Pulse monitor.

set CC_PULSE_URL=http://localhost:8765
set SESSION_ID=%CLAUDE_SESSION_ID%
set PROJECT_PATH=%CLAUDE_PROJECT_DIR%
set ENDPOINT=%1

if "%ENDPOINT%"=="" set ENDPOINT=idle
if "%SESSION_ID%"=="" set SESSION_ID=unknown

REM Build JSON payload
if "%PROJECT_PATH%"=="" (
    set PAYLOAD={"sessionId":"%SESSION_ID%"}
) else (
    set PAYLOAD={"sessionId":"%SESSION_ID%","projectPath":"%PROJECT_PATH%"}
)

REM Send the status update (fire-and-forget)
start /b curl -s -X POST "%CC_PULSE_URL%/%ENDPOINT%" -H "Content-Type: application/json" -d "%PAYLOAD%" --max-time 2 >nul 2>&1

exit /b 0
