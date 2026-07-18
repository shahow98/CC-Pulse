#!/bin/bash
# CC-Pulse Hook Script
# This script sends Claude Code session status updates to the CC-Pulse monitor.
# It is called by Claude Code's hook system.

# The hook receives environment variables:
#   CLAUDE_SESSION_ID - the current session ID
#   CLAUDE_PROJECT_DIR - the current project directory (optional)

CC_PULSE_URL="http://localhost:8765"
SESSION_ID="${CLAUDE_SESSION_ID:-unknown}"
PROJECT_PATH="${CLAUDE_PROJECT_DIR:-}"

# The first argument is the status endpoint: start, busy, idle, interactive, end
ENDPOINT="${1:-idle}"

# Build JSON payload
if [ -n "$PROJECT_PATH" ]; then
    PAYLOAD="{\"sessionId\":\"$SESSION_ID\",\"projectPath\":\"$PROJECT_PATH\"}"
else
    PAYLOAD="{\"sessionId\":\"$SESSION_ID\"}"
fi

# Send the status update (fire-and-forget, don't block Claude Code)
curl -s -X POST "$CC_PULSE_URL/$ENDPOINT" \
    -H "Content-Type: application/json" \
    -d "$PAYLOAD" \
    --max-time 2 \
    > /dev/null 2>&1 &

exit 0
