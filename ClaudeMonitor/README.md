# CC-Pulse

A lightweight Windows desktop monitor for Claude Code session status.

## What It Does

CC-Pulse runs in the system tray and displays a floating status window that shows the state of your active Claude Code sessions using a traffic-light color system:

| Color | Status | Meaning |
|-------|--------|---------|
| 🟢 Green | Idle | Session is idle, between tasks |
| 🟡 Yellow | Busy | Session is actively processing / using tools |
| 🔴 Red | Interactive | Session is waiting for your input |

## How It Works

1. **CC-Pulse** starts an HTTP listener on `localhost:8765`
2. **Claude Code hooks** send POST requests to the listener when session state changes
3. The tray icon and floating window update in real-time

## Prerequisites

- Windows 10/11
- .NET 8.0 Runtime ([download](https://dotnet.microsoft.com/download/dotnet/8.0))

## Build

```bash
cd ClaudeMonitor
dotnet build
```

## Run

```bash
dotnet run
```

## Publish

Framework-dependent (small, ~190KB, requires .NET 8 runtime):
```bash
dotnet publish -r win-x64 -c Release
```

Self-contained (large, ~155MB, no runtime needed):
```bash
dotnet publish -r win-x64 -c Release -p:SelfContained=true -p:TrimMode=partial
```

## Hook Configuration

The Claude Code hooks are configured in `.claude/settings.json`. They call `ClaudeMonitor/Hooks/cc-pulse-hook.cmd` with the appropriate status endpoint:

| Hook Event | Endpoint | Status |
|------------|----------|--------|
| SessionStart | `/start` | Idle (green) |
| PreToolUse | `/busy` | Busy (yellow) |
| PostToolUse | `/idle` | Idle (green) |
| UserPromptSubmit | `/interactive` | Interactive (red) |

## API Endpoints

All endpoints accept POST with JSON body: `{"sessionId": "...", "projectPath": "..."}`

- `POST /start` — Register a new session
- `POST /busy` — Session started tool use
- `POST /idle` — Session finished tool use
- `POST /interactive` — Session waiting for user input
- `POST /end` — Session terminated

## Project Structure

```
ClaudeMonitor/
├── Models/
│   └── SessionInfo.cs          # Session data model + status enum
├── Services/
│   ├── HookServer.cs           # HTTP listener on localhost:8765
│   ├── SessionManager.cs       # Thread-safe session state management
│   └── TrayManager.cs          # System tray icon + context menu
├── UI/
│   ├── StatusWindow.xaml       # Floating topmost status window
│   └── StatusWindow.xaml.cs    # Code-behind with data binding
├── Assets/Icons/               # Tray icon files (green/yellow/red)
├── Hooks/                      # Claude Code hook scripts
├── App.xaml / App.xaml.cs      # WPF app entry, lifecycle management
└── ClaudeMonitor.csproj        # Project configuration
```
