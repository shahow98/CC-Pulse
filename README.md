# CC-Pulse

**English** | [中文](./README.zh-CN.md)

A lightweight Windows system tray monitor for [Claude Code](https://claude.ai/code) sessions. CC-Pulse displays a traffic-light indicator in your system tray and a floating on-top window so you can see at a glance whether Claude is idle, working, or waiting for your input.

## Features

- **Traffic-light tray icon** — green (idle), yellow (working), red (waiting for input)
- **Floating status window** — always-on-top, draggable card showing all active sessions
- **Multi-session support** — tracks multiple Claude Code sessions simultaneously
- **Zero dependencies** — built entirely on .NET 8 built-in APIs (no NuGet packages)
- **Tiny footprint** — framework-dependent build is ~190 KB; self-contained build available

## How It Works

CC-Pulse runs a local HTTP server on `localhost:8765` that receives webhook events from Claude Code hooks. Each event updates the session's status:

| Route | Meaning | Indicator |
|-------|---------|-----------|
| `POST /start` | New session started | 🟢 Idle |
| `POST /busy` | Session is using tools | 🟡 Working |
| `POST /idle` | Session finished a task | 🟢 Idle |
| `POST /interactive` | Session is waiting for user input | 🔴 Waiting |
| `POST /end` | Session ended | Removed |

When a session transitions from **Busy → Idle**, CC-Pulse starts a 10-second timer. If no new activity occurs within that window, the session is automatically marked as **Interactive** (red) — a heuristic that detects when Claude is waiting for you to answer a question.

## Prerequisites

- Windows 10/11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (for framework-dependent build)
- [Claude Code](https://claude.ai/code) CLI

## Build

```bash
# Framework-dependent (requires .NET 8 runtime on target machine, ~190 KB)
dotnet publish ClaudeMonitor/ClaudeMonitor.csproj -c Release

# Self-contained (no runtime needed, ~155 MB)
dotnet publish ClaudeMonitor/ClaudeMonitor.csproj -c Release -p:SelfContained=true -p:TrimMode=partial
```

The output is in `ClaudeMonitor/bin/Release/net8.0-windows/win-x64/publish/`.

## Configure Claude Code Hooks

Add the following to your Claude Code settings file (`.claude/settings.json` in your project or home directory):

```json
{
  "hooks": {
    "SessionStart": [
      {
        "type": "command",
        "command": "curl -s -X POST http://localhost:8765/start -H \"Content-Type: application/json\" -d \"{\\\"sessionId\\\": \\\"$CLAUDE_SESSION_ID\\\", \\\"projectPath\\\": \\\"$CLAUDE_PROJECT_DIR\\\"}\""
      }
    ],
    "PreToolUse": [
      {
        "type": "command",
        "command": "curl -s -X POST http://localhost:8765/busy -H \"Content-Type: application/json\" -d \"{\\\"sessionId\\\": \\\"$CLAUDE_SESSION_ID\\\"}\""
      }
    ],
    "PostToolUse": [
      {
        "type": "command",
        "command": "curl -s -X POST http://localhost:8765/idle -H \"Content-Type: application/json\" -d \"{\\\"sessionId\\\": \\\"$CLAUDE_SESSION_ID\\\"}\""
      }
    ]
  }
}
```

> **Note:** The `interactive` state is detected automatically via the idle timeout heuristic — no explicit hook is needed. The `end` route can be wired to a `SessionEnd` hook if Claude Code supports it in your version.

## Usage

1. **Launch CC-Pulse** — run `ClaudeMonitor.exe`. A green tray icon appears.
2. **Start Claude Code** — open a terminal and run `claude`. The session appears in the floating window.
3. **Monitor** — the tray icon and floating window update in real time as Claude works.
4. **Interact** — double-click the tray icon or right-click → "Show/Hide Window" to toggle the floating card. Right-click → "Exit" to quit.

The floating window can be dragged to any position on screen. Clicking ✕ minimizes it to the tray.

## Project Structure

```
ClaudeMonitor/
├── App.xaml / App.xaml.cs       # Application lifecycle
├── Models/
│   └── SessionInfo.cs           # Session state model + status enum
├── Services/
│   ├── HookServer.cs            # HTTP listener (localhost:8765)
│   ├── SessionManager.cs        # Thread-safe session state management
│   └── TrayManager.cs           # System tray icon with context menu
├── UI/
│   ├── StatusWindow.xaml        # Floating card UI layout
│   └── StatusWindow.xaml.cs     # Window logic + value converters
└── Assets/
    └── Icons/                   # Tray icons (green/yellow/red/app .ico)
```

## Tech Stack

| Component | Technology | Rationale |
|-----------|-----------|-----------|
| Language | C# (.NET 8) | High performance, native Windows support |
| Tray icon | WinForms `NotifyIcon` | Most stable tray API on Windows, near-zero overhead |
| Floating window | WPF | Hardware-accelerated, flexible layout, `Topmost` support |
| HTTP server | `HttpListener` | Built-in, no ASP.NET Core overhead |
| JSON parsing | `System.Text.Json` | Built-in, high performance |

## License

MIT
