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

Hooks are **auto-configured** on first launch — no manual editing of `settings.json` needed. A dedicated console-mode proxy (`CC-Pulse-Hook.exe`) reliably reads session context from stdin and forwards it to the HTTP server.

## Prerequisites

- Windows 10/11
- .NET 8.0 Runtime ([download](https://dotnet.microsoft.com/download/dotnet/8.0))

## Build

```bash
# Framework-dependent (small, ~190KB, requires .NET 8 runtime)
dotnet publish -r win-x64 -c Release

# Self-contained (large, ~155MB, no runtime needed)
dotnet publish -r win-x64 -c Release -p:SelfContained=true -p:TrimMode=partial
```

## Run

```bash
dotnet run
```

## Hook Configuration

Hooks are auto-configured on first launch. To manage manually:

```bash
ClaudeMonitor.exe configure-hooks    # Add CC-Pulse hooks to settings.json
ClaudeMonitor.exe remove-hooks       # Remove CC-Pulse hooks from settings.json
```

### Hook Events

| Hook Event | Endpoint | Status |
|------------|----------|--------|
| `SessionStart` | `/start` | Idle (green) |
| `PreToolUse` | `/busy` | Busy (yellow) |
| `PostToolUse` | `/idle` | Idle (green) |
| `UserPromptSubmit` | `/busy` | Busy (yellow) |
| `Stop` | `/idle` | Idle (green) |
| `SessionEnd` | `/end` | Removed |

## API Endpoints

All endpoints accept POST with JSON body: `{"sessionId": "...", "projectPath": "..."}`

- `POST /start` — Register a new session
- `POST /busy` — Session started tool use
- `POST /idle` — Session finished tool use
- `POST /interactive` — Session waiting for user input
- `POST /end` — Session terminated
- `GET /sessions` — List all active sessions (diagnostic)

## CLI Commands

```bash
ClaudeMonitor.exe hook <endpoint>       # Send status update (start|busy|idle|interactive|end)
ClaudeMonitor.exe configure-hooks       # Add CC-Pulse hooks to settings.json
ClaudeMonitor.exe remove-hooks          # Remove CC-Pulse hooks from settings.json
ClaudeMonitor.exe stop-process          # Stop running ClaudeMonitor process
```

## Project Structure

```
ClaudeMonitor/
├── App.xaml / App.xaml.cs           # WPF app entry, lifecycle + CLI command routing
├── Models/
│   └── SessionInfo.cs               # Session data model + status enum
├── Services/
│   ├── AppSettings.cs               # Persistent settings (language) with locale auto-detect
│   ├── HookConfigurator.cs          # Auto-configure/remove hooks in settings.json
│   ├── HookRunner.cs                # CLI hook runner (reads stdin, POSTs to HookServer)
│   ├── HookServer.cs                # HTTP listener on localhost:8765
│   ├── Lang.cs                      # Bilingual string lookup (en / zh-CN)
│   ├── SessionManager.cs            # Thread-safe session state management
│   └── TrayManager.cs               # System tray icon + context menu + language switcher
├── UI/
│   ├── StatusWindow.xaml            # Floating topmost status window
│   └── StatusWindow.xaml.cs         # Code-behind with data binding
├── Hooks/
│   └── cc-pulse-hook.sh             # Bash hook script (for Git Bash / WSL)
├── Assets/Icons/                     # Tray icon files (green/yellow/red/app)
└── ClaudeMonitor.csproj             # Project configuration

ClaudeMonitor.HookProxy/
├── Program.cs                        # Console-mode hook proxy (reliable stdin reading)
└── ClaudeMonitor.HookProxy.csproj    # Published as CC-Pulse-Hook.exe
```
