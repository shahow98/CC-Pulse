# CC-Pulse

**English** | [中文](./README.zh-CN.md)

A lightweight Windows system tray monitor for [Claude Code](https://claude.ai/code) sessions. CC-Pulse displays a traffic-light indicator in your system tray and a floating on-top window so you can see at a glance whether Claude is idle, working, or waiting for your input.

## Features

- **Traffic-light tray icon** — green (idle), yellow (working), red (waiting for input)
- **Floating status window** — always-on-top, draggable card showing all active sessions
- **Multi-session support** — tracks multiple Claude Code sessions simultaneously
- **Auto-configure hooks** — hooks are set up automatically on first launch; no manual editing needed
- **Bilingual UI** — English and Chinese (简体中文), auto-detected from system locale, switchable from tray menu
- **MSI installer** — one-click install with desktop shortcut, auto-start, and clean uninstall
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

When Claude Code emits a **Notification** event (e.g. asking a question or waiting for user confirmation), CC-Pulse immediately marks the session as **Interactive** (red). The light stays yellow between tool calls and only turns green when the `Stop` event fires — meaning Claude has finished its turn.

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

CC-Pulse **automatically configures** Claude Code hooks on first launch. No manual editing of `settings.json` is required.

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
| `SessionStart` | `/start` | Idle (green) |
| `PreToolUse` | `/busy` | Busy (yellow) |
| `PostToolUse` | `/busy` | Busy (yellow) |
| `UserPromptSubmit` | `/busy` | Busy (yellow) |
| `Notification` | `/interactive` | Interactive (red) |
| `Stop` | `/idle` | Idle (green) |
| `SessionEnd` | `/end` | Removed |

> **Note:** The `interactive` state is triggered by the `Notification` hook event — when Claude asks a question or waits for user input, it emits a notification that CC-Pulse catches and turns the light red.

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
│   └── SessionInfo.cs               # Session state model + status enum
├── Services/
│   ├── AppSettings.cs               # Persistent settings (language) with locale auto-detect
│   ├── HookConfigurator.cs          # Auto-configure/remove hooks in settings.json
│   ├── HookRunner.cs                # CLI hook runner (reads stdin, POSTs to HookServer)
│   ├── HookServer.cs                # HTTP listener (localhost:8765)
│   ├── Lang.cs                      # Bilingual string lookup (en / zh-CN)
│   ├── SessionManager.cs            # Thread-safe session state management
│   └── TrayManager.cs               # System tray icon with context menu + language switcher
├── UI/
│   ├── StatusWindow.xaml            # Floating card UI layout
│   └── StatusWindow.xaml.cs         # Window logic + value converters
├── Hooks/
│   └── cc-pulse-hook.sh             # Bash hook script (for Git Bash / WSL)
└── Assets/
    └── Icons/                        # Tray icons (green/yellow/red/app .ico)

ClaudeMonitor.HookProxy/
├── Program.cs                        # Console-mode hook proxy (reliable stdin reading)
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
