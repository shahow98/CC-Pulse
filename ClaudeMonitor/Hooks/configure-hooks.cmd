@echo off
REM Configure Claude Code hooks for CC-Pulse after installation
REM This script updates ~/.claude/settings.json to add CC-Pulse hook entries

setlocal enabledelayedexpansion

set "SETTINGS_FILE=%USERPROFILE%\.claude\settings.json"
set "HOOK_PATH=%~dp0..\Hooks\cc-pulse-hook.sh"

REM Convert hook path to forward slashes for Git Bash compatibility
set "HOOK_PATH_FWD=%HOOK_PATH:\=/%"

REM Remove trailing slash if present
if "%HOOK_PATH_FWD:~-1%"=="/" set "HOOK_PATH_FWD=%HOOK_PATH_FWD:~0,-1%"

REM Use Python to safely update the JSON settings file
py -3 -c "
import json, os, sys

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

# Define CC-Pulse hooks
hooks_config = {
    'SessionStart': [
        {'matcher': 'startup', 'hooks': [{'type': 'command', 'command': f'bash {hook_path} start', 'timeout': 5}]},
        {'matcher': 'clear', 'hooks': [{'type': 'command', 'command': f'bash {hook_path} start', 'timeout': 5}]},
        {'matcher': 'compact', 'hooks': [{'type': 'command', 'command': f'bash {hook_path} start', 'timeout': 5}]}
    ],
    'PreToolUse': [
        {'matcher': '', 'hooks': [{'type': 'command', 'command': f'bash {hook_path} busy', 'timeout': 5}]}
    ],
    'PostToolUse': [
        {'matcher': '', 'hooks': [{'type': 'command', 'command': f'bash {hook_path} idle', 'timeout': 5}]}
    ],
    'UserPromptSubmit': [
        {'matcher': '', 'hooks': [{'type': 'command', 'command': f'bash {hook_path} busy', 'timeout': 5}]}
    ],
    'Stop': [
        {'matcher': '', 'hooks': [{'type': 'command', 'command': f'bash {hook_path} idle', 'timeout': 5}]}
    ],
    'SessionEnd': [
        {'matcher': '', 'hooks': [{'type': 'command', 'command': f'bash {hook_path} end', 'timeout': 5}]}
    ]
}

# Merge hooks into existing settings
if 'hooks' not in settings:
    settings['hooks'] = {}

for event, entries in hooks_config.items():
    if event not in settings['hooks']:
        settings['hooks'][event] = entries
    else:
        # Check if CC-Pulse hooks already exist for this event
        existing_commands = set()
        for entry in settings['hooks'][event]:
            for hook in entry.get('hooks', []):
                cmd = hook.get('command', '')
                if 'cc-pulse-hook' in cmd:
                    existing_commands.add(entry.get('matcher', ''))

        # Add CC-Pulse entries that don't already exist
        for entry in entries:
            matcher = entry.get('matcher', '')
            if matcher not in existing_commands:
                settings['hooks'][event].append(entry)

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
