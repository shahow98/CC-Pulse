#!/usr/bin/env bash
# CC-Pulse Hook Script (Bash - for Git Bash / WSL environments)
# Sends Claude Code session status updates to the CC-Pulse monitor.
# Reads session_id from stdin JSON (Claude Code passes hook context via stdin).

CC_PULSE_URL="http://localhost:8765"
ENDPOINT="${1:-idle}"

# Read JSON from stdin (Claude Code passes hook context via stdin)
INPUT=""
if [ ! -t 0 ]; then
    INPUT=$(cat)
fi

# Extract session_id and cwd from stdin JSON using pure bash (no jq dependency)
SESSION_ID=""
PROJECT_PATH=""
if [ -n "$INPUT" ]; then
    # Extract "session_id": "value" — handle both double and no quotes around value
    SESSION_ID=$(echo "$INPUT" | grep -o '"session_id"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 | sed 's/.*:.*"\([^"]*\)"/\1/')
    PROJECT_PATH=$(echo "$INPUT" | grep -o '"cwd"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 | sed 's/.*:.*"\([^"]*\)"/\1/')
fi

# Fallback to environment variables if stdin didn't provide them
SESSION_ID="${SESSION_ID:-${CLAUDE_SESSION_ID:-unknown}}"
PROJECT_PATH="${PROJECT_PATH:-${CLAUDE_PROJECT_DIR:-}}"

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
