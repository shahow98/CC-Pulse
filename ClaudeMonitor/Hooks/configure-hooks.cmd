@echo off
REM Configure Claude Code hooks for CC-Pulse after installation
REM This script updates ~/.claude/settings.json to add CC-Pulse hook entries
REM It preserves all existing non-CC-Pulse hooks by appending into existing entries

setlocal enabledelayedexpansion

set "SETTINGS_FILE=%USERPROFILE%\.claude\settings.json"
set "HOOK_PATH=%~dp0cc-pulse-hook.sh"

REM Convert hook path to forward slashes for Git Bash compatibility
set "HOOK_PATH_FWD=%HOOK_PATH:\=/%"

REM Remove trailing slash if present
if "%HOOK_PATH_FWD:~-1%"=="/" set "HOOK_PATH_FWD=%HOOK_PATH_FWD:~0,-1%"

REM Use Python to safely update the JSON settings file
py -3 -c "
import json, os

settings_path = os.path.expanduser('~/.claude/settings.json')
hook_path = r'%HOOK_PATH_FWD%'.replace('\\\\', '/')

# Ensure .claude directory exists
os.makedirs(os.path.dirname(settings_path), exist_ok=True)

# Load existing settings or create new
if os.path.exists(settings_path):
    with open(settings_path, 'r', encoding='utf-8') as f:
        settings = json.load(f)
else:
    settings = {}

# Define CC-Pulse hooks: event -> list of (matcher, hook_command_suffix)
hooks_config = {
    'SessionStart': [
        ('startup', 'start'),
        ('clear', 'start'),
        ('compact', 'start'),
    ],
    'PreToolUse': [
        ('', 'busy'),
    ],
    'PostToolUse': [
        ('', 'idle'),
    ],
    'UserPromptSubmit': [
        ('', 'busy'),
    ],
    'Stop': [
        ('', 'idle'),
    ],
    'SessionEnd': [
        ('', 'end'),
    ],
}

# Ensure hooks section exists
if 'hooks' not in settings:
    settings['hooks'] = {}

for event, entries in hooks_config.items():
    if event not in settings['hooks']:
        settings['hooks'][event] = []

    for matcher, suffix in entries:
        cc_pulse_hook = {
            'type': 'command',
            'command': f'bash {hook_path} {suffix}',
            'timeout': 5,
        }

        # Search for an existing entry with the same matcher
        existing_entry = None
        for entry in settings['hooks'][event]:
            if entry.get('matcher', '') == matcher:
                existing_entry = entry
                break

        if existing_entry is not None:
            # Check if CC-Pulse hook already exists in this entry
            already_exists = any(
                'cc-pulse-hook' in h.get('command', '')
                for h in existing_entry.get('hooks', [])
            )
            if not already_exists:
                # Append CC-Pulse hook into the existing entry
                existing_entry['hooks'].append(cc_pulse_hook)
        else:
            # No entry with this matcher — create a new one
            settings['hooks'][event].append({
                'matcher': matcher,
                'hooks': [cc_pulse_hook],
            })

# Write back
with open(settings_path, 'w', encoding='utf-8') as f:
    json.dump(settings, f, indent=2, ensure_ascii=False)

print(f'CC-Pulse hooks configured in {settings_path}')
" 2>&1

if %ERRORLEVEL% NEQ 0 (
    echo Warning: Failed to configure Claude Code hooks automatically.
    echo You can manually add hooks to %SETTINGS_FILE%
    echo Hook script path: %HOOK_PATH_FWD%
)

exit /b 0
