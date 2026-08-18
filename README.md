# CC-Pulse

**English** | [中文](./README.zh-CN.md)

A lightweight Windows system tray monitor for [Claude Code](https://claude.ai/code) sessions. CC-Pulse displays a traffic-light indicator in your system tray and a floating on-top window so you can see at a glance whether Claude is working or idle.

## Features

- **Traffic-light tray icon** — red (working), green (idle / waiting for input)
- **Floating status window** — always-on-top, draggable card showing all active sessions
- **Multi-session support** — tracks multiple Claude Code sessions simultaneously
- **Subagent awareness** — detects Task/Agent subagents by name and shows each as its own working/idle row
- **Fine-grained status** — distinguishes *thinking*, *running: \<tool\>*, *waiting for API…*, and *waiting for input…* instead of a generic "Working"
- **Dual-channel state machine** — fuses low-latency hook events with authoritative transcript JSONL tailing for accurate, flicker-free status
- **Auto-configure hooks** — hooks are inserted on launch and removed on exit; no manual editing needed
- **Bilingual UI** — English and Chinese (简体中文), auto-detected from system locale, switchable from tray menu
- **MSI installer** — one-click install with desktop shortcut, auto-start, and clean uninstall
- **Zero dependencies** — built entirely on .NET 8 built-in APIs (no NuGet packages)
- **Tiny footprint** — framework-dependent build is ~190 KB; self-contained build available

## How It Works

CC-Pulse combines two complementary channels to track each session's status:

1. **Hook channel (low latency)** — a local HTTP server on `localhost:8765` receives webhook events from Claude Code hooks. Hooks fire the instant something happens, so the tray reacts immediately.
2. **Transcript channel (authoritative)** — a JSONL tailer watches each session's transcript file (`<sessionId>.jsonl`) under `~/.claude/projects/...` and parses `user` / `assistant` / `tool_use` / `tool_result` / `system` entries. The transcript is the ground truth, so it corrects hook lag, missed events, and ghost sessions.

A fusion state machine in `SessionManager` reconciles both channels: hooks provide fast triggers, the transcript provides the authoritative fine-grained state. The main agent is refined into five states — `Idle`, `Thinking`, `ToolRunning`, `WaitingApi`, `WaitingUser` — which reduce to the coarse tray color (Busy → red, Idle → green). Subagents (spawned by the `Task`/`Agent` tool) are detected via a filesystem watcher and each tailed from its own `agent-<id>.jsonl` transcript, shown by name as a separate working/idle row.

### Hook Routes

| Route | Meaning | Indicator |
|-------|---------|-----------|
| `POST /start` | New session started (`startup`/`clear`/`resume`); `compact` continues the current session | 🟢 Idle |
| `POST /busy` | Session is working (prompt submitted, tool starting/ending) | 🔴 Working |
| `POST /idle` | Session finished a turn (`Stop`) | 🟢 Idle |
| `POST /interactive` | Session is waiting for user input (`Notification`) | 🟢 Idle |
| `POST /subagent-stop` | A subagent finished (removed from the subagent list) | — |
| `POST /stopfailure` | Turn ended on API error (rate limit, network) | 🟢 Idle |
| `POST /end` | Session ended | Removed |

When Claude Code is actively working (thinking, generating text, or using tools), the light turns **red**. When Claude finishes its turn (`Stop` event) or is waiting for user input (`Notification` event), the light turns **green**.

## Prerequisites

- Windows 10/11
- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (for framework-dependent build; [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build from source)
- [Claude Code](https://claude.ai/code) CLI

## Install (MSI)

Download the MSI installer from [Releases](../../releases) and run it. The installer will:

1. Install CC-Pulse to `Program Files\CC-Pulse\`
2. Create a desktop shortcut (optional)
3. Register auto-start via registry (optional)
4. Auto-configure Claude Code hooks in `~/.claude/settings.json`

To uninstall, use Windows Settings → Apps or run the MSI again.

## Build from Source

```bash
# Framework-dependent (requires .NET 8 runtime on target machine, ~190 KB)
dotnet publish ClaudeMonitor/ClaudeMonitor.csproj -r win-x64 -c Release

# Self-contained (no runtime needed, ~155 MB)
dotnet publish ClaudeMonitor/ClaudeMonitor.csproj -r win-x64 -c Release -p:SelfContained=true -p:TrimMode=partial
```

The output is in `ClaudeMonitor/bin/Release/net8.0-windows/win-x64/publish/`.

### Build MSI Installer

Requires [WiX v5](https://wixtoolset.org/) (`dotnet tool install --global wix --version 5.0.2`):

```powershell
# PowerShell
./build-msi.ps1

# Or bash
./build-msi.sh
```

## Hook Configuration

CC-Pulse **automatically inserts** its Claude Code hooks on launch and **removes them on exit**, so `~/.claude/settings.json` stays clean when CC-Pulse isn't running. No manual editing is required.

If you need to reconfigure or remove hooks manually:

```bash
# Re-configure hooks
ClaudeMonitor.exe configure-hooks

# Remove hooks
ClaudeMonitor.exe remove-hooks
```

The hooks use a dedicated **console-mode proxy** (`CC-Pulse-Hook.exe`) that reliably reads session context from stdin and forwards it to the CC-Pulse HTTP server. This avoids the stdin pipe issues that GUI subsystem executables can have on Windows.

### Hook Events

| Hook Event | Endpoint | Status |
|------------|----------|--------|
| `SessionStart` | `/start` | Idle (green); `compact` source preserves state |
| `PreToolUse` | `/busy` | Busy (red); `Agent` tool marks a subagent active |
| `PostToolUse` | `/busy` | Busy (red) |
| `UserPromptSubmit` | `/busy` | Busy (red) |
| `Notification` | `/interactive` | Idle (green) |
| `Stop` | `/idle` | Idle (green) |
| `StopFailure` | `/stopfailure` | Idle (green) — turn ended on API error |
| `SubagentStop` | `/subagent-stop` | Subagent removed |
| `SessionEnd` | `/end` | Removed |

> **Note:** The `Notification` hook event fires when Claude asks a question or waits for user input — CC-Pulse treats this as Idle (green), since Claude is no longer actively working. The `SubagentStop` and `StopFailure` events are forwarded by the hook proxy alongside the standard events.

## Usage

1. **Launch CC-Pulse** — run `ClaudeMonitor.exe` (or use the desktop shortcut). A green tray icon appears.
2. **Start Claude Code** — open a terminal and run `claude`. The session appears in the floating window.
3. **Monitor** — the tray icon and floating window update in real time as Claude works.
4. **Interact** — double-click the tray icon or right-click → "Show/Hide Window" to toggle the floating card. Right-click → "Language" to switch UI language. Right-click → "Exit" to quit.

The floating window can be dragged to any position on screen. Clicking ✕ minimizes it to the tray.

## CLI Commands

CC-Pulse also supports CLI sub-commands (useful for scripting or troubleshooting):

```bash
ClaudeMonitor.exe hook <endpoint>       # Send status update (start|busy|idle|interactive|end)
ClaudeMonitor.exe configure-hooks       # Add CC-Pulse hooks to settings.json
ClaudeMonitor.exe remove-hooks          # Remove CC-Pulse hooks from settings.json
ClaudeMonitor.exe stop-process          # Stop running ClaudeMonitor process
```

## Project Structure

```
ClaudeMonitor/
├── App.xaml / App.xaml.cs           # Application lifecycle + CLI command routing
├── Models/
│   ├── AnomalyRecord.cs             # Recorded state anomaly (hook/transcript mismatch)
│   ├── MainAgentState.cs            # Fine-grained main-agent state enum (Idle/Thinking/ToolRunning/WaitingApi/WaitingUser)
│   ├── SessionInfo.cs               # Session state model + status enum + active-tool tracking
│   └── SubagentInfo.cs              # Subagent model + fine-grained state enum
├── Services/
│   ├── AppSettings.cs               # Persistent settings (language) with locale auto-detect
│   ├── ClaudePaths.cs               # Resolve ~/.claude project/transcript paths
│   ├── FileLogger.cs                # Runtime state-machine diagnosis log
│   ├── HookConfigurator.cs          # Auto-configure/remove hooks in settings.json
│   ├── HookRunner.cs                # CLI hook runner (reads stdin, POSTs to HookServer)
│   ├── HookServer.cs                # HTTP listener (localhost:8765) + route dispatch
│   ├── Lang.cs                      # Bilingual string lookup (en / zh-CN)
│   ├── QueueManager.cs              # Durable hook-event queue (survives launch race)
│   ├── SessionManager.cs            # Thread-safe session state + hook/transcript fusion state machine
│   ├── SubagentTailer.cs            # Tail each subagent's agent-<id>.jsonl transcript
│   ├── SubagentWatcher.cs           # Filesystem watcher detecting subagent transcripts by name
│   ├── TranscriptTailer.cs          # Tail each session's <sessionId>.jsonl transcript (authoritative)
│   └── TrayManager.cs               # System tray icon with context menu + language switcher
├── UI/
│   ├── StatusWindow.xaml            # Floating card UI layout
│   └── StatusWindow.xaml.cs         # Window logic + value converters
├── Hooks/
│   └── cc-pulse-hook.sh             # Bash hook script (for Git Bash / WSL)
└── Assets/
    └── Icons/                        # Tray icons (green/red/app .ico)

ClaudeMonitor.HookProxy/
├── Program.cs                        # Console-mode hook proxy (reliable stdin reading)
├── QueueManager.cs                   # Queues hook events for the proxy to drain
└── ClaudeMonitor.HookProxy.csproj    # Published as CC-Pulse-Hook.exe

Installer/
├── CC-Pulse.wxs                      # WiX v5 installer definition
└── License.rtf                       # EULA for MSI installer
```

## Tech Stack

| Component | Technology | Rationale |
|-----------|-----------|-----------|
| Language | C# (.NET 8) | High performance, native Windows support |
| Tray icon | WinForms `NotifyIcon` | Most stable tray API on Windows, near-zero overhead |
| Floating window | WPF | Hardware-accelerated, flexible layout, `Topmost` support |
| HTTP server | `HttpListener` | Built-in, no ASP.NET Core overhead |
| JSON parsing | `System.Text.Json` | Built-in, high performance |
| Hook proxy | Console-mode .NET exe | Reliable stdin pipe inheritance on Windows |
| Installer | WiX v5 | Professional MSI with custom UI, auto-start, hook config |

## License

MIT
