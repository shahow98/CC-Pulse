@echo off
REM Remove CC-Pulse hooks from Claude Code settings on uninstall
REM This script only removes individual CC-Pulse hook entries, preserving all other hooks

setlocal enabledelayedexpansion

set "SETTINGS_FILE=%USERPROFILE%\.claude\settings.json"

if not exist "%SETTINGS_FILE%" exit /b 0

REM Use Python to safely remove CC-Pulse hooks from JSON
py -3 -c "
import json, os

settings_path = os.path.expanduser('~/.claude/settings.json')

if not os.path.exists(settings_path):
    exit(0)

with open(settings_path, 'r', encoding='utf-8') as f:
    settings = json.load(f)

if 'hooks' not in settings:
    exit(0)

# Remove only CC-Pulse hooks from each entry, preserving non-CC-Pulse hooks
events_to_clean = list(settings['hooks'].keys())
for event in events_to_clean:
    entries = settings['hooks'][event]
    for entry in entries:
        # Remove only hooks containing 'cc-pulse-hook' from the hooks array
        entry['hooks'] = [
            h for h in entry.get('hooks', [])
            if 'cc-pulse-hook' not in h.get('command', '')
        ]

    # Remove entries that now have empty hooks arrays
    settings['hooks'][event] = [
        e for e in entries if e.get('hooks', [])
    ]

    # Remove empty matcher key if it's empty string (clean up)
    for entry in settings['hooks'][event]:
        if 'matcher' in entry and entry['matcher'] == '':
            # Keep the key for consistency, but no special handling needed
            pass

# Remove empty event arrays
empty_events = [k for k, v in settings['hooks'].items() if not v]
for k in empty_events:
    del settings['hooks'][k]

# Remove empty hooks object
if not settings['hooks']:
    del settings['hooks']

with open(settings_path, 'w', encoding='utf-8') as f:
    json.dump(settings, f, indent=2, ensure_ascii=False)

print('CC-Pulse hooks removed from settings')
" 2>&1

exit /b 0
