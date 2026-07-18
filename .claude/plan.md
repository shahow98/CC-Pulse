# Fix: CC-Pulse Hooks Not Detecting Sessions

## Root Cause Analysis

There are **three bugs** causing sessions to not be detected:

### Bug 1: GUI Subsystem — stdin not received (PRIMARY ISSUE)
`ClaudeMonitor.exe` is built as `WinExe` (GUI subsystem). On Windows, GUI subsystem processes **detach from the parent console**, and stdin pipes may not be properly inherited when spawned via shell form. Since `CLAUDE_SESSION_ID` is NOT available as an environment variable in hook processes (only in stdin JSON), the session ID falls back to `"unknown"`.

Evidence:
- `file` command shows: `PE32+ executable (GUI) x86-64`
- GitHub issues confirm `CLAUDE_SESSION_ID` is NOT exposed as env var to hooks
- The Coding Agent Explorer (.NET) uses a separate console-mode `HookAgent.exe` for this exact reason

### Bug 2: Missing `resume` matcher for SessionStart
The `SessionStart` hooks only configure matchers for `startup`, `clear`, and `compact`. When a session is resumed via `--resume`, `--continue`, or `/resume`, no `SessionStart` hook fires, so CC-Pulse never detects the session.

### Bug 3: Environment variable fallback uses wrong variable name
`HookRunner` falls back to `CLAUDE_SESSION_ID` env var, but this variable is NOT set in hook processes. `CLAUDE_CODE_SESSION_ID` is only available in Bash tool subprocesses, not in hook processes. The stdin JSON is the only reliable source.

## Fix Plan

### Fix 1: Create a lightweight console-mode hook proxy (`CC-Pulse-Hook.exe`)

Create a new console application project `ClaudeMonitor.HookProxy` that:
- Is built as `Exe` (console subsystem) — properly inherits stdin
- Reads JSON from stdin, extracts `session_id` and `cwd`
- Falls back to `CLAUDE_SESSION_ID` / `CLAUDE_CODE_SESSION_ID` env vars
- POSTs to `http://localhost:8765/{endpoint}` (same as HookRunner)
- Is a minimal single-file .NET 8 console app (~few KB)

This mirrors the Coding Agent Explorer's approach of using a separate `HookAgent.exe`.

**Files to create:**
- `ClaudeMonitor.HookProxy/Program.cs` — minimal console app
- `ClaudeMonitor.HookProxy/ClaudeMonitor.HookProxy.csproj` — console project

### Fix 2: Update HookConfigurator to use the hook proxy

Change hook commands from shell form:
```json
"command": "\"C:/Program Files/CC-Pulse/ClaudeMonitor.exe\" hook start"
```
to exec form:
```json
"command": "C:/Program Files/CC-Pulse/CC-Pulse-Hook.exe",
"args": ["start"]
```

Also add the `resume` matcher for `SessionStart`.

**Files to modify:**
- `ClaudeMonitor/Services/HookConfigurator.cs`

### Fix 3: Update HookRunner env var fallback

Add `CLAUDE_CODE_SESSION_ID` as an additional fallback in `HookRunner.cs` (for the case where the main exe is still used directly).

**Files to modify:**
- `ClaudeMonitor/Services/HookRunner.cs`

### Fix 4: Update build/publish to include the hook proxy

Add the hook proxy to the publish workflow and WiX installer.

**Files to modify:**
- `Installer/CC-Pulse.wxs` — add CC-Pulse-Hook.exe
- Any publish scripts

### Fix 5: Update AreHooksConfigured detection

Update the hook detection to also recognize `CC-Pulse-Hook` in commands.

**Files to modify:**
- `ClaudeMonitor/Services/HookConfigurator.cs`
