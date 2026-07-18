这是一个基于 **C# (.NET 8)** 的完整技术方案，旨在实现**极小体积**和**极低内存占用**。我们将采用“混合 UI”策略：使用 WinForms 处理托盘（最稳定、资源最少），使用 WPF 处理置顶窗口（UI 灵活、美观）。

### 1. 核心技术栈清单

| 组件 | 技术选型 | 理由 |
| :--- | :--- | :--- |
| **开发语言** | C# (.NET 8) | 性能优异，跨平台潜力，Windows 原生支持最好。 |
| **IDE** | VS Code + C# Dev Kit | 轻量级，启动快，完全满足开发需求。 |
| **托盘图标** | `System.Windows.Forms.NotifyIcon` | Windows 上最成熟的托盘方案，几乎零开销。 |
| **置顶窗口** | `WPF (Windows Presentation Foundation)` | 支持硬件加速，UI 布局灵活，轻松实现 `Topmost`。 |
| **HTTP 服务** | `System.Net.HttpListener` | .NET 内置类，无需引入 ASP.NET Core 重型框架，内存占用极低。 |
| **JSON 解析** | `System.Text.Json` | .NET 内置，高性能，无第三方依赖。 |
| **打包发布** | Native AOT 或 Self-contained | 目标是将最终 exe 控制在 10MB 以内，内存 < 20MB。 |

---

### 2. 详细开发步骤

#### 第一阶段：环境搭建与项目初始化
1.  **安装 SDK**：下载并安装 **.NET 8.0 SDK**。
2.  **配置 VS Code**：安装 `C# Dev Kit` 扩展。
3.  **创建项目**：
    *   创建一个 WPF 应用模板：`dotnet new wpf -n ClaudeMonitor`。
    *   在 `.csproj` 文件中确保启用了 Windows 桌面支持。
    *   手动添加对 `System.Windows.Forms` 的引用（用于托盘）。

#### 第二阶段：核心逻辑开发（后端）
1.  **实现 HTTP 监听器 (`HookServer.cs`)**：
    *   使用 `HttpListener` 在 `localhost:8765` 启动监听。
    *   编写异步方法处理 `POST` 请求，解析 JSON 数据。
    *   定义一个简单的状态枚举：`Idle` (绿), `Busy` (黄), `Interactive` (红)。
2.  **实现会话管理器 (`SessionManager.cs`)**：
    *   维护一个 `ConcurrentDictionary<string, SessionInfo>` 来存储所有活跃的 Claude Code 会话。
    *   提供线程安全的方法：`AddSession`, `UpdateStatus`, `RemoveSession`。
    *   当状态改变时，触发一个 C# 事件 (`EventHandler`) 通知 UI 更新。

#### 第三阶段：UI 界面开发（前端）
1.  **托盘管理 (`TrayManager.cs`)**：
    *   在程序启动时创建 `NotifyIcon`。
    *   设置图标（可以使用简单的彩色圆点 .ico 文件）。
    *   添加右键菜单：包含“退出”、“显示/隐藏窗口”选项。
    *   **关键点**：确保程序关闭时正确释放托盘图标资源。
2.  **置顶小窗口 (`StatusWindow.xaml`)**：
    *   设置窗口属性：`WindowStyle="None"` (无边框), `ResizeMode="NoResize"`, `Topmost="True"`, `ShowInTaskbar="False"`。
    *   **UI 布局**：使用 `ListBox` 或 `ItemsControl`。
    *   **数据模板**：为每个会话项设计模板，包含：
        *   会话 ID/名称（文本块）。
        *   状态指示灯（使用一个圆形 `Ellipse`，根据绑定状态改变 `Fill` 颜色）。
3.  **数据绑定**：
    *   将 `SessionManager` 的数据集合绑定到 WPF 窗口的 `ItemsSource`。
    *   使用 `INotifyPropertyChanged` 确保当 HTTP 服务器收到新状态时，UI 自动刷新红绿灯颜色。

#### 第四阶段：Claude Code 侧配置
1.  **创建 Hook 脚本**：
    *   编写简单的 Shell/Batch 脚本或使用 `curl` 命令。
    *   在 Claude Code 的 `.claude/settings.json` 中配置 `hooks`。
    *   映射事件：
        *   `SessionStart` -> POST `/start`
        *   `PreToolUse` -> POST `/busy`
        *   `PostToolUse` -> POST `/idle`
        *   `UserPromptSubmit` -> POST `/interactive` (需配合超时逻辑判断)

#### 第五阶段：优化与发布
1.  **性能优化**：
    *   确保 HTTP 监听在后台线程运行，不阻塞 UI 主线程。
    *   UI 更新使用 `Dispatcher.Invoke` 确保线程安全。
2.  **资源最小化**：
    *   移除所有不必要的 NuGet 包。
    *   使用 `PublishSingleFile=true` 和 `TrimMode=full` 进行发布。
3.  **生成安装包**：
    *   使用 `dotnet publish -r win-x64 -c Release` 生成最终的 `.exe` 文件。

---

### 3. 关键技术难点与解决方案

*   **难点 1：如何准确识别“红灯”（交互状态）？**
    *   *方案*：Claude Code 没有直接的“等待输入”Hook。建议在 `UserPromptSubmit` 后启动一个计时器。如果在 X 秒内没有触发 `PreToolUse`，则判定为进入“交互/等待用户回答”状态，切换为红灯。一旦触发 `PreToolUse`，立即切回黄灯。
*   **难点 2：WPF 与 WinForms 混用冲突？**
    *   *方案*：在 `Program.cs` 中先初始化 WPF 应用，然后在后台线程或主线程中实例化 `NotifyIcon`。只要不创建 WinForms 的主窗体，两者可以和平共处。
*   **难点 3：窗口如何始终保持在最前但不干扰操作？**
    *   *方案*：设置 `Topmost=True`，但可以将窗口设置为“点击穿透”（如果需要）或者保持很小的尺寸放在屏幕角落。

这个方案能确保你的工具在后台运行时几乎“隐形”，只有在需要关注 Claude Code 状态时才通过红绿灯提醒用户。