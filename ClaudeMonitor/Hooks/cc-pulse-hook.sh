#!/usr/bin/env bash
# CC-Pulse Hook Script (Bash - for Git Bash / WSL environments)
# Delegates to ClaudeMonitor.exe for status updates.
# This script is called by Claude Code hooks configured in settings.json.

# Resolve the directory where this script lives (same dir as ClaudeMonitor.exe)
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
EXE_PATH="$SCRIPT_DIR/ClaudeMonitor.exe"

# Forward the endpoint argument and stdin to ClaudeMonitor.exe hook
"$EXE_PATH" hook "${1:-idle}"

exit 0
