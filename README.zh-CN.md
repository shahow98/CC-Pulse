# CC-Pulse

[English](./README.md) | **中文**

一个轻量级的 Windows 系统托盘监控工具，用于 [Claude Code](https://claude.ai/code) 会话。CC-Pulse 在系统托盘和悬浮置顶窗口中显示红绿灯状态指示，让你一眼就能看到 Claude 是工作中还是空闲。

## 功能特性

- **红绿灯托盘图标** — 红色（工作中）、绿色（空闲 / 等待输入）
- **悬浮状态窗口** — 始终置顶、可拖动的卡片，显示所有活跃会话
- **多会话支持** — 同时追踪多个 Claude Code 会话
- **自动配置钩子** — 首次启动时自动配置 Claude Code 钩子，无需手动编辑
- **双语界面** — 支持英文和简体中文，自动检测系统语言，可从托盘菜单切换
- **MSI 安装包** — 一键安装，支持桌面快捷方式、开机自启、干净卸载
- **零依赖** — 完全基于 .NET 8 内置 API 构建，无需任何 NuGet 包
- **极小体积** — 框架依赖版约 190 KB；亦提供独立部署版

## 工作原理

CC-Pulse 在 `localhost:8765` 运行一个本地 HTTP 服务器，接收来自 Claude Code 钩子的 webhook 事件。每个事件更新对应会话的状态：

| 路由 | 含义 | 指示灯 |
|------|------|--------|
| `POST /start` | 新会话启动 | 🟢 空闲 |
| `POST /busy` | 会话正在工作（思考、生成、使用工具） | 🔴 工作中 |
| `POST /idle` | 会话完成一项任务 | 🟢 空闲 |
| `POST /interactive` | 会话等待用户输入 | 🟢 空闲 |
| `POST /end` | 会话结束 | 移除 |

当 Claude Code 正在工作（思考、生成文本或使用工具）时，指示灯变为**红色**。当 Claude 完成当前回合（`Stop` 事件）或等待用户输入（`Notification` 事件）时，指示灯变为**绿色**。

## 前置要求

- Windows 10/11
- [.NET 8.0 运行时](https://dotnet.microsoft.com/download/dotnet/8.0)（框架依赖版需要；[.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 用于从源码构建）
- [Claude Code](https://claude.ai/code) CLI

## 安装（MSI）

从 [Releases](../../releases) 下载 MSI 安装包并运行。安装程序将：

1. 将 CC-Pulse 安装到 `Program Files\CC-Pulse\`
2. 创建桌面快捷方式（可选）
3. 通过注册表设置开机自启（可选）
4. 自动配置 Claude Code 钩子到 `~/.claude/settings.json`

卸载请使用 Windows 设置 → 应用，或再次运行 MSI。

## 从源码构建

```bash
# 框架依赖版（目标机器需安装 .NET 8 运行时，约 190 KB）
dotnet publish ClaudeMonitor/ClaudeMonitor.csproj -r win-x64 -c Release

# 独立部署版（无需运行时，约 155 MB）
dotnet publish ClaudeMonitor/ClaudeMonitor.csproj -r win-x64 -c Release -p:SelfContained=true -p:TrimMode=partial
```

输出目录为 `ClaudeMonitor/bin/Release/net8.0-windows/win-x64/publish/`。

### 构建 MSI 安装包

需要 [WiX v5](https://wixtoolset.org/)（`dotnet tool install --global wix --version 5.0.2`）：

```powershell
# PowerShell
./build-msi.ps1

# 或 bash
./build-msi.sh
```

## 钩子配置

CC-Pulse 在首次启动时会**自动配置** Claude Code 钩子，无需手动编辑 `settings.json`。

如需手动重新配置或移除钩子：

```bash
# 重新配置钩子
ClaudeMonitor.exe configure-hooks

# 移除钩子
ClaudeMonitor.exe remove-hooks
```

钩子使用专用的**控制台模式代理**（`CC-Pulse-Hook.exe`），可靠地从 stdin 读取会话上下文并转发到 CC-Pulse HTTP 服务器，避免了 Windows 上 GUI 子系统可执行文件的 stdin 管道问题。

### 钩子事件

| 钩子事件 | 端点 | 状态 |
|---------|------|------|
| `SessionStart` | `/start` | 空闲（绿色） |
| `PreToolUse` | `/busy` | 工作中（红色） |
| `PostToolUse` | `/busy` | 工作中（红色） |
| `UserPromptSubmit` | `/busy` | 工作中（红色） |
| `Notification` | `/interactive` | 空闲（绿色） |
| `Stop` | `/idle` | 空闲（绿色） |
| `SessionEnd` | `/end` | 移除 |

> **注意：** `Notification` 钩子事件在 Claude 提问或等待用户输入时触发——CC-Pulse 将此视为空闲（绿色），因为 Claude 不再主动工作。

## 使用方法

1. **启动 CC-Pulse** — 运行 `ClaudeMonitor.exe`（或使用桌面快捷方式），系统托盘出现绿色图标。
2. **启动 Claude Code** — 打开终端运行 `claude`，会话出现在悬浮窗口中。
3. **监控状态** — 托盘图标和悬浮窗口随 Claude 的工作状态实时更新。
4. **交互操作** — 双击托盘图标或右键 → "Show/Hide Window" 切换悬浮卡片显示；右键 → "Language" 切换界面语言；右键 → "Exit" 退出程序。

悬浮窗口可拖动到屏幕任意位置。点击 ✕ 按钮最小化到托盘。

## 命令行

CC-Pulse 还支持命令行子命令（适用于脚本或故障排查）：

```bash
ClaudeMonitor.exe hook <endpoint>       # 发送状态更新 (start|busy|idle|interactive|end)
ClaudeMonitor.exe configure-hooks       # 添加 CC-Pulse 钩子到 settings.json
ClaudeMonitor.exe remove-hooks          # 从 settings.json 移除 CC-Pulse 钩子
ClaudeMonitor.exe stop-process          # 停止运行中的 ClaudeMonitor 进程
```

## 项目结构

```
ClaudeMonitor/
├── App.xaml / App.xaml.cs           # 应用程序生命周期管理 + CLI 命令路由
├── Models/
│   └── SessionInfo.cs               # 会话状态模型 + 状态枚举
├── Services/
│   ├── AppSettings.cs               # 持久化设置（语言），支持系统语言自动检测
│   ├── HookConfigurator.cs          # 自动配置/移除 settings.json 中的钩子
│   ├── HookRunner.cs                # CLI 钩子运行器（读取 stdin，POST 到 HookServer）
│   ├── HookServer.cs                # HTTP 监听器 (localhost:8765)
│   ├── Lang.cs                      # 双语字符串查找 (en / zh-CN)
│   ├── SessionManager.cs            # 线程安全的会话状态管理
│   └── TrayManager.cs               # 系统托盘图标及右键菜单 + 语言切换
├── UI/
│   ├── StatusWindow.xaml            # 悬浮卡片 UI 布局
│   └── StatusWindow.xaml.cs         # 窗口逻辑 + 值转换器
├── Hooks/
│   └── cc-pulse-hook.sh             # Bash 钩子脚本（用于 Git Bash / WSL）
└── Assets/
    └── Icons/                        # 托盘图标 (green/red/app .ico)

ClaudeMonitor.HookProxy/
├── Program.cs                        # 控制台模式钩子代理（可靠的 stdin 读取）
└── ClaudeMonitor.HookProxy.csproj    # 发布为 CC-Pulse-Hook.exe

Installer/
├── CC-Pulse.wxs                      # WiX v5 安装包定义
└── License.rtf                       # MSI 安装包的 EULA
```

## 技术栈

| 组件 | 技术选型 | 选型理由 |
|------|---------|---------|
| 开发语言 | C# (.NET 8) | 高性能，Windows 原生支持最佳 |
| 托盘图标 | WinForms `NotifyIcon` | Windows 上最稳定的托盘方案，几乎零开销 |
| 悬浮窗口 | WPF | 硬件加速，布局灵活，原生支持 `Topmost` |
| HTTP 服务器 | `HttpListener` | .NET 内置，无需引入 ASP.NET Core 重型框架 |
| JSON 解析 | `System.Text.Json` | .NET 内置，高性能 |
| 钩子代理 | 控制台模式 .NET exe | Windows 上可靠的 stdin 管道继承 |
| 安装包 | WiX v5 | 专业 MSI 安装包，支持自定义 UI、开机自启、钩子配置 |

## 许可证

MIT
