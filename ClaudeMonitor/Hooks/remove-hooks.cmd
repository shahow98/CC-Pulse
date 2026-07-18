@echo off
REM Remove CC-Pulse hooks from Claude Code settings on uninstall

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

# Remove CC-Pulse hook entries from each event
events_to_clean = list(settings['hooks'].keys())
for event in events_to_clean:
    entries = settings['hooks'][event]
    filtered = []
    for entry in entries:
        has_cc_pulse = False
        for hook in entry.get('hooks', []):
            cmd = hook.get('command', '')
            if 'cc-pulse-hook' in cmd:
                has_cc_pulse = True
                break
        if not has_cc_pulse:
            filtered.append(entry)
    settings['hooks'][event] = filtered

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
