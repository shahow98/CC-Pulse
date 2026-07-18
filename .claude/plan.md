# Plan: Fix MSI Uninstall + Hook Safety

## Problem 1: Running process blocks uninstall
If `ClaudeMonitor.exe` is running during MSI uninstall, files are locked and can't be deleted.

**Fix:** Add a `stop-process.cmd` custom action that runs `taskkill /IM ClaudeMonitor.exe /F` before `RemoveFiles`.

## Problem 2: `configure-hooks.cmd` can overwrite non-CC-Pulse hooks
Current logic: if a `matcher` already exists in an event, it skips the entire entry. But it should **append** CC-Pulse hooks into the existing entry's `hooks` array when the matcher matches, rather than creating a duplicate entry with the same matcher.

Example scenario: user already has `PreToolUse` with `matcher: ""` containing their own hook. Current code adds a **second** entry with `matcher: ""` — Claude Code may not handle duplicate matchers well. Instead, CC-Pulse should append its hook into the existing entry.

**Fix:** Rewrite the merge logic:
- For each CC-Pulse hook entry, search for an existing entry with the same `matcher`
- If found AND CC-Pulse hook not already in that entry's `hooks` array → append CC-Pulse hook to that entry
- If not found → add the entire new entry

## Problem 3: `remove-hooks.cmd` removes entire entries, destroying non-CC-Pulse hooks
Current logic: if any hook in an entry contains `cc-pulse-hook`, the **entire entry** (including non-CC-Pulse hooks) is removed.

Example: `SessionStart/matcher:"startup"` has both `session-start.py` and `cc-pulse-hook.sh`. Remove deletes the whole entry, losing `session-start.py`.

**Fix:** Rewrite the removal logic:
- For each entry, remove only the individual hooks containing `cc-pulse-hook` from the `hooks` array
- If the entry's `hooks` array becomes empty after removal → remove the entry
- If the event's entries array becomes empty → remove the event
- If the `hooks` object becomes empty → remove it

## Files to modify

1. **`ClaudeMonitor/Hooks/configure-hooks.cmd`** — Fix merge logic to append into existing entries
2. **`ClaudeMonitor/Hooks/remove-hooks.cmd`** — Fix removal to only strip CC-Pulse hooks, not entire entries
3. **`ClaudeMonitor/Hooks/stop-process.cmd`** — New file: `taskkill /IM ClaudeMonitor.exe /F`
4. **`Installer/CC-Pulse.wxs`** — Add `stop-process.cmd` as installed file + custom action before `RemoveFiles`
5. **`build-msi.sh`** — No changes needed (already includes HooksDir)

## Verification
- Simulate configure with existing non-CC-Pulse hooks → CC-Pulse appended, not duplicated
- Simulate remove with mixed entries → only CC-Pulse hooks stripped, others preserved
- Rebuild MSI
