#!/usr/bin/env bash
# CC-Pulse Hook Script (Bash - for Git Bash / WSL environments)
# Sends Claude Code session status updates to the CC-Pulse monitor.

CC_PULSE_URL="http://localhost:8765"
SESSION_ID="${CLAUDE_SESSION_ID:-unknown}"
PROJECT_PATH="${CLAUDE_PROJECT_DIR:-}"
ENDPOINT="${1:-idle}"

# Build JSON payload
if [ -z "$PROJECT_PATH" ]; then
    PAYLOAD="{\"sessionId\":\"${SESSION_ID}\"}"
else
    PAYLOAD="{\"sessionId\":\"${SESSION_ID}\",\"projectPath\":\"${PROJECT_PATH}\"}"
fi

# Send the status update (fire-and-forget, 2s timeout)
curl -s -X POST "${CC_PULSE_URL}/${ENDPOINT}" \
    -H "Content-Type: application/json" \
    -d "$PAYLOAD" \
    --max-time 2 \
    >/dev/null 2>&1 &

exit 0
