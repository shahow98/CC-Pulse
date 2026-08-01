# 调整 Hook 生命周期：启动时插入，关闭时移除

## 背景

当前 CC-Pulse 在 `OnStartup` 调用 `EnsureHooksConfigured()` 把 hooks 写入 `~/.claude/settings.json`，但 `OnExit` 只清理内存资源，不移除 hooks。结果是 hooks 一旦插入就永久驻留，即使 CC-Pulse 未运行，Claude Code 每次触发事件仍会向 `localhost:8765` 发请求（无人监听 → 5 秒 timeout 后失败）。

目标：**用户启动 CC-Pulse 时插入 hooks，关闭 CC-Pulse 时移除本应用相关 hooks**。

## 决策（已与用户确认）

1. **异常退出残留**：仅靠 `OnExit` 清理。崩溃/强杀时残留的 hook 在 CC-Pulse 未运行时只是向无人监听的端口发请求快速失败，影响小；下次启动 `EnsureHooksConfigured` 会先迁移/重插，不会重复堆积。不做启动时兜底清理。
2. **迁移逻辑保留**：`EnsureHooksConfigured` 仍检测旧格式/旧端点/缺失 hook，需要时先 Remove 再 Configure。退出时也调用 Remove。
3. **CLI 子命令保留**：`configure-hooks` / `remove-hooks` 继续可用，供手动排错。

## 改动

### 1. `ClaudeMonitor/App.xaml.cs` — `OnExit` 增加 hook 移除

在 `OnExit` 清理资源的开头（在 `base.OnExit` 之前）调用 `HookConfigurator.Remove()`，与现有内存资源清理放在一起。包在 try/catch 里，失败只写 `Debug.WriteLine`，不弹窗（退出阶段弹 MessageBox 体验差，且 hook 残留非致命）。

```csharp
protected override void OnExit(ExitEventArgs e)
{
    // Remove CC-Pulse hooks from Claude Code settings so they don't
    // linger (and fire against a dead HookServer) while CC-Pulse is closed.
    // Re-inserted on next launch by EnsureHooksConfigured.
    try
    {
        HookConfigurator.Remove();
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"Failed to remove CC-Pulse hooks on exit: {ex.Message}");
    }

    // Clean up resources in reverse order
    _hookServer?.Dispose();
    _subagentWatcher?.Dispose();
    _trayManager?.Dispose();
    _sessionManager?.Dispose();
    AppSettings.Instance.Dispose();

    base.OnExit(e);
}
```

### 2. `ClaudeMonitor/App.xaml.cs` — `OnStartup` 注释更新

`EnsureHooksConfigured()` 调用本身不变（它已经做"不存在则插入、旧格式则迁移"），但上方注释从 "Auto-configure hooks on first launch (or if not yet configured)" 改为反映新语义："Insert CC-Pulse hooks on launch (removed again on exit)"。`EnsureHooksConfigured` 方法体不动——它的"不存在则 Configure、旧格式则 Remove+Configure"行为正好满足"启动时确保 hooks 存在且为最新格式"。

### 3. `ClaudeMonitor/App.xaml.cs` — 类头 XML 注释更新

类注释里描述生命周期的部分补一句：hooks 在启动时插入、退出时移除。

## 不改动

- `HookConfigurator.cs`：`Configure` / `Remove` / 各 `Uses*` 检测方法全部保留原样。
- `EnsureHooksConfigured` 方法体：保留迁移逻辑。
- CLI 子命令 `configure-hooks` / `remove-hooks`：保留。
- `App.xaml` `ShutdownMode="OnExplicitShutdown"`：已保证只有显式 `Shutdown()` 才退出，`OnExit` 必然触发（托盘退出、窗口关闭走 `OnExitRequested` → `Shutdown`）。

## 验证

- 启动应用 → 检查 `~/.claude/settings.json` 含 CC-Pulse hooks。
- 退出应用（托盘 Exit）→ 检查 settings.json 中 CC-Pulse hooks 已被移除，其他非 CC-Pulse hooks 保留。
- 再次启动 → hooks 重新出现。
- 旧格式迁移：手动把 settings.json 改成 shell-form `ClaudeMonitor.exe hook start`，启动后应自动迁移为 exec form。
