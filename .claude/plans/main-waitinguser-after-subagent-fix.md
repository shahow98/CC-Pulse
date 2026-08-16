# 修复 subagent 完成后 main agent 总结工作时状态卡在 WaitingUser

## 问题现象

用户观察：subagent 工作状态显示正常，但 **subagent 结束后 main agent 在总结 subagent 工作成果时，状态仍显示"等待输入"（WaitingUser），而实际 main agent 正在工作**。

## 现场日志（cc-pulse-2026-08-16.log，session 01701a70，已逐行核实）

时间线：
- `18:16:06` main agent 调用 `Agent` 工具启动 subagent `ae3295fafcbcd8b2b`
- `18:16:18` main transcript 写 `stop_hook_summary`（main 把控制权交给 subagent，main 进入 Idle）
- `18:16:11 ~ 18:18:47` subagent 工作（main 显示 Idle，subagent 行显示状态）—— 期间 main 的 `IsWaitingUser` 被某 Notification 设为 true（无文件日志，见根因）
- `18:18:47` subagent `task-notification` completed → `UpdateSubagentState` 清 `SubagentActive` → `Reconcile` 恢复运行 → 立刻读到 `IsWaitingUser=true` → **main-fine = WaitingUser**（日志 284 行：`waitingUser=True`）
- `18:18:47 ~ 18:20:38` **main agent 实际在总结 subagent 成果（transcript 持续写 tool_use/tool_result），但状态卡在 WaitingUser**
- `18:20:38` main `transcript turn_end`（main 真正结束这一轮）

对比 session 885d8859（00:41）同样复现：subagent Completed 后 13 秒，main-fine 从 Idle 翻成 WaitingUser（`waitingUser=True`），直到下一个有 hook 的 turn 才清除。

## 根因（三层）

### 层 1：subagent 期间的 Notification 被误判为"等待用户输入"

全局 `~/.claude/settings.json` 配置了 `Notification` hook → `interactive` 路由 → `HookServer.HandleNotification`。subagent 运行期间 Claude Code 会发 Notification（subagent 需要权限审批 / subagent 完成通知等），其文案可能含 `permission` / `can you`+`?` 等关键词，被 `IsUserActionRequired`（HookServer.cs:286）判定为 true，于是调用 `SetSubagentActive(false)` + `SetWaitingUser`（HookServer.cs:266,271）。

但 subagent 活跃时 main agent 是在**等 subagent**，不是等用户。把 main 标成 WaitingUser 是语义错误。`HandleNotification` 只写 `Debug.WriteLine`（不写 FileLogger），所以日志里看不到这个触发点——这也是排查难点。

### 层 2：`IsWaitingUser` 清除条件太窄，transcript 路径不清

`_isWaitingUser = true` 只在 `SessionInfo.SetWaitingUser()` 设，只被 `SessionManager.SetWaitingUser`（274 行）调用，只被 `HandleNotification`（271 行）调用。

`_isWaitingUser = false` 的清除点：
- `ClearWaitingUser()`（SessionInfo.cs:459）—— 只被 `SessionManager.UpdateStatus` 在 `newStatus == Busy` 时调用（SessionManager.cs:207）
- `ClearMainFineStateTracking()`（SessionInfo.cs:473）—— 会话重置时

**问题**：subagent 完成后 main agent 总结工作时，PreToolUse hook 全部丢失（日志有 `hookTracked=False` + `hook_missed` 异常），所以 `UpdateStatus(Busy)` 永不触发。main agent 的工作完全通过 transcript 推进（`OnTranscriptToolUse` / `OnTranscriptAssistantMessage` → `Reconcile` → `ApplyReconciledStatus`），但 **transcript 路径的 `ApplyReconciledStatus` 和 `OnTranscriptToolUse` 都不调 `ClearWaitingUser`**。于是 `IsWaitingUser` 卡死为 true，`DeriveMainFineState`（SessionManager.cs:1333）在 coarse=Idle 时优先返回 `WaitingUser`，状态永远显示"等待输入"，直到下一个真正有 hook 的 turn。

### 层 3：`DeriveMainFineState` 在 coarse=Idle 时无条件优先 WaitingUser

SessionManager.cs:1331-1336：
```csharp
if (coarse == SessionStatus.Idle)
{
    return session.IsWaitingUser
        ? (MainAgentState.WaitingUser, string.Empty)
        : (MainAgentState.Idle, string.Empty);
}
```
subagent 完成瞬间，`TranscriptLastStopUtc` 仍是 subagent 启动前那个 turn_end（18:16:18），所以 `DeriveMainState` 走规则 2 返回 Idle（SessionManager.cs:1245）。coarse=Idle + `IsWaitingUser=true` → WaitingUser。即使 main agent 随后开始总结（transcript 写 tool_use → coarse 翻 Busy），层 2 的清除缺失仍让 WaitingUser 在 Idle 间隙反复出现。

## 修复方案（全面修复，三处互补）

### 改动 1：subagent 活跃时 Notification 不标 main 为 WaitingUser

`ClaudeMonitor/Services/HookServer.cs` — `HandleNotification`（259-278 行）

当 `IsUserActionRequired` 为 true 时，**若 subagent 正活跃**，不调 `SetWaitingUser`（main 在等 subagent，不是等用户）。仍可调 `SetSubagentActive(false)` 吗？**不能**——subagent 活跃期间的"permission" Notification 很可能是 **subagent 自己**的工具需要审批，不代表 subagent 结束。保守做法：subagent 活跃时，Notification 既不清 subagent 标志、也不标 WaitingUser，只刷新 busy timer（与"背景通知"分支一致）。

```csharp
if (IsUserActionRequired(message, title, notifType))
{
    if (_sessionManager.IsSubagentActive(sessionId))
    {
        // Subagent is running — the main agent is waiting on the subagent,
        // not on the user. A permission/input notification during this window
        // almost certainly belongs to the subagent's own tooling, not a main-
        // agent block. Do NOT mark main WaitingUser and do NOT clear the
        // subagent flag; just keep the timer alive.
        FileLogger.Info(
            $"notification (subagent active, ignored) session={sessionId} " +
            $"type={notifType} title={title}");
        _sessionManager.ResetBusyTimeout(sessionId);
        return;
    }
    _sessionManager.SetSubagentActive(sessionId, false);
    _sessionManager.SetWaitingUser(sessionId);
    FileLogger.Info(
        $"notification (waiting user) session={sessionId} " +
        $"type={notifType} title={title}");
}
else
{
    FileLogger.Info(
        $"notification (background) session={sessionId} " +
        $"type={notifType} title={title}");
    _sessionManager.ResetBusyTimeout(sessionId);
}
```

这同时给 HandleNotification 加了 FileLogger 日志（层 1 排查难点），所有分支都记录原始 payload 字段。

### 改动 2：transcript 路径在 main agent 恢复活动时清 IsWaitingUser

`ClaudeMonitor/Services/SessionManager.cs` — `OnTranscriptToolUse`（916 行）和 `OnTranscriptAssistantMessage`（990 行）

main agent 重新开始工作（transcript 观察到 tool_use 或 assistant 消息）即表示它不再等待用户——清 `IsWaitingUser`。这是层 2 的根本修复：不依赖可能丢失的 hook，transcript 是权威源。

在 `OnTranscriptToolUse` 开头（`RecordTranscriptToolUse` 之后）加：
```csharp
// The main agent is authoritatively active (transcript tool_use). If a stale
// IsWaitingUser flag from a prior Notification lingers (e.g. set during a
// subagent run, then the post-subagent summary turn's PreToolUse hooks were
// lost so UpdateStatus(Busy) never cleared it), clear it here so the fine
// state leaves WaitingUser. Transcript is the authority; hooks may be lost.
if (session.IsWaitingUser)
{
    session.ClearWaitingUser();
    FileLogger.Info($"transcript tool_use cleared waitingUser {sessionId}");
}
```

在 `OnTranscriptAssistantMessage`（`RecordTranscriptAssistantMessage` 之后）加同样的清除：main agent 产出 assistant 文本（如总结 subagent 成果的开场白）也是"不再等用户"的权威信号。

注意：`OnTranscriptToolUse` 已在 subagent 的 tool_use 上被调用吗？不会——subagent 的 transcript 是独立文件，由 `SubagentTailer` 处理，不经过 main 的 `OnTranscriptToolUse`。main 的 `OnTranscriptToolUse` 只对 main transcript 的 tool_use 触发，所以这里清除是安全的（只清 main 的 WaitingUser）。

### 改动 3：`DeriveMainFineState` 在 coarse=Busy 时不返回 WaitingUser（防御性，已隐式满足）

层 3 当前代码在 coarse=Busy 时根本不走 WaitingUser 分支（WaitingUser 只在 coarse=Idle 分支），所以 main agent 一旦开始总结（transcript tool_use → coarse=Busy），fine state 会是 ToolRunning/Thinking 而非 WaitingUser。**改动 2** 确保了 coarse 在 Idle 间隙时 `IsWaitingUser` 也已被清，所以 WaitingUser 不会再误现。

无需改 `DeriveMainFineState` 本身——改动 2 从源头清除了误判标志。保留此节作为分析记录。

## 不做

- 不改 `IsUserActionRequired` 的关键词列表（subagent 活跃时直接短路，比逐条精修关键词更稳健；非 subagent 场景的误判是另一独立问题）。
- 不改 `SubagentState` 枚举或 subagent 终态路径（那是已实现的 subagent-flicker-fix 范围）。
- 不改 hook 配置（全局 Notification hook 保留，用于非 subagent 的真实权限审批场景）。
- 不在 `ApplyReconciledStatus` 清 WaitingUser（那里是状态应用点，不是"main 恢复活动"的语义点；在 `OnTranscriptToolUse`/`OnTranscriptAssistantMessage` 清更精准，且避免 Idle→Idle reconcile 时误清）。

## 验证

- `dotnet build` 通过。
- 手动场景：启动 subagent → subagent 期间若有 Notification → main 不应变 WaitingUser（日志应见 `notification (subagent active, ignored)`）；subagent 完成 → main 总结工作 → 状态应为 Thinking/ToolRunning（日志见 `transcript tool_use cleared waitingUser` 若之前被误标），不应卡 WaitingUser。
- 日志：新增 `notification (...)` 三类日志行 + `transcript tool_use cleared waitingUser`，便于追溯。
- 回归：非 subagent 的真实权限审批仍应正确显示 WaitingUser（改动 1 只在 subagent 活跃时短路）。
