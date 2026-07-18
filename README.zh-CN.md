# CC-Pulse

[English](./README.md) | **中文**

一个轻量级的 Windows 系统托盘监控工具，用于 [Claude Code](https://claude.ai/code) 会话。CC-Pulse 在系统托盘和悬浮置顶窗口中显示红绿灯状态指示，让你一眼就能看到 Claude 是空闲、工作中，还是在等待你的输入。

## 功能特性

- **红绿灯托盘图标** — 绿色（空闲）、黄色（工作中）、红色（等待输入）
- **悬浮状态窗口** — 始终置顶、可拖动的卡片，显示所有活跃会话
- **多会话支持** — 同时追踪多个 Claude Code 会话
- **零依赖** — 完全基于 .NET 8 内置 API 构建，无需任何 NuGet 包
- **极小体积** — 框架依赖版约 190 KB；亦提供独立部署版

## 工作原理

CC-Pulse 在 `localhost:8765` 运行一个本地 HTTP 服务器，接收来自 Claude Code 钩子的 webhook 事件。每个事件更新对应会话的状态：

| 路由 | 含义 | 指示灯 |
|------|------|--------|
| `POST /start` | 新会话启动 | 🟢 空闲 |
| `POST /busy` | 会话正在使用工具 | 🟡 工作中 |
| `POST /idle` | 会话完成一项任务 | 🟢 空闲 |
| `POST /interactive` | 会话等待用户输入 | 🔴 等待输入 |
| `POST /end` | 会话结束 | 移除 |

当会话从 **工作中 → 空闲** 转换时，CC-Pulse 启动一个 10 秒计时器。如果在该时间窗口内没有新的活动，会话将自动标记为 **等待输入**（红色）—— 这是一种启发式检测，用于判断 Claude 是否正在等待你回答问题。

## 前置要求

- Windows 10/11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（框架依赖版需要）
- [Claude Code](https://claude.ai/code) CLI

## 构建

```bash
# 框架依赖版（目标机器需安装 .NET 8 运行时，约 190 KB）
dotnet publish ClaudeMonitor/ClaudeMonitor.csproj -c Release

# 独立部署版（无需运行时，约 155 MB）
dotnet publish ClaudeMonitor/ClaudeMonitor.csproj -c Release -p:SelfContained=true -p:TrimMode=partial
```

输出目录为 `ClaudeMonitor/bin/Release/net8.0-windows/win-x64/publish/`。

## 配置 Claude Code 钩子

将以下内容添加到 Claude Code 的设置文件中（项目或用户目录下的 `.claude/settings.json`）：

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

> **注意：** `interactive`（等待输入）状态通过空闲超时启发式自动检测，无需显式配置钩子。如果你的 Claude Code 版本支持 `SessionEnd` 事件，可以将其连接到 `POST /end` 路由。

## 使用方法

1. **启动 CC-Pulse** — 运行 `ClaudeMonitor.exe`，系统托盘出现绿色图标。
2. **启动 Claude Code** — 打开终端运行 `claude`，会话出现在悬浮窗口中。
3. **监控状态** — 托盘图标和悬浮窗口随 Claude 的工作状态实时更新。
4. **交互操作** — 双击托盘图标或右键 → "Show/Hide Window" 切换悬浮卡片显示；右键 → "Exit" 退出程序。

悬浮窗口可拖动到屏幕任意位置。点击 ✕ 按钮最小化到托盘。

## 项目结构

```
ClaudeMonitor/
├── App.xaml / App.xaml.cs       # 应用程序生命周期管理
├── Models/
│   └── SessionInfo.cs           # 会话状态模型 + 状态枚举
├── Services/
│   ├── HookServer.cs            # HTTP 监听器 (localhost:8765)
│   ├── SessionManager.cs        # 线程安全的会话状态管理
│   └── TrayManager.cs           # 系统托盘图标及右键菜单
├── UI/
│   ├── StatusWindow.xaml        # 悬浮卡片 UI 布局
│   └── StatusWindow.xaml.cs     # 窗口逻辑 + 值转换器
└── Assets/
    └── Icons/                   # 托盘图标 (green/yellow/red/app .ico)
```

## 技术栈

| 组件 | 技术选型 | 选型理由 |
|------|---------|---------|
| 开发语言 | C# (.NET 8) | 高性能，Windows 原生支持最佳 |
| 托盘图标 | WinForms `NotifyIcon` | Windows 上最稳定的托盘方案，几乎零开销 |
| 悬浮窗口 | WPF | 硬件加速，布局灵活，原生支持 `Topmost` |
| HTTP 服务器 | `HttpListener` | .NET 内置，无需引入 ASP.NET Core 重型框架 |
| JSON 解析 | `System.Text.Json` | .NET 内置，高性能 |

## 许可证

MIT
