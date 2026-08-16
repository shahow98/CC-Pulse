# 修复 subagent 假终态闪烁：用 main transcript 权威终止信号取代静默阈值

## 问题

凌晨日志（`cc-pulse-2026-08-16.log`）确认：subagent 长时间执行时会"先消失，过一会重新变成启动中"。根因是 **40s 静默→Completed 的假终态判定** + **escape hatch 对中断事件的误判**。

### 两个现场（已用 transcript 文件逐行核实）

**Subagent 2 `ad860f22`**（跑在带 `IsTailing` 守卫的未提交代码上，仍闪烁）：
- `16:40:54` 最后一次活动 → 静默 99.8s（用户在犹豫）
- `16:41:34` tailer 40s 静默阈值触发 → `Completed`（**假终态**）
- `16:41:37` terminal removal → row 消失（用户看到"消失"）
- `16:42:33` 用户中断，subagent transcript 写 `[Request interrupted by user]`
- `16:42:34` escape hatch 检测到 `lastActivity > termAt` → 误判为"假终态恢复" → row 变 `Pending`（用户看到"重新变成启动中"）

**Subagent 1 `af7f5329`**（正常完成，也被误判）：
- `16:28:05` 最后活动 → 静默 221s（subagent 在生成长总结）
- `16:31:46` subagent 正常输出总结完成，但 CC-Pulse 显示 `Pending`（tail 被销毁重建丢历史）

### 根本原因

1. **40s 静默阈值不可靠**：subagent 长思考/生成总结/等用户审批时，transcript 长时间无新行，但 subagent 并未结束。阈值一到就误判 Completed。
2. **escape hatch 无法区分"恢复"与"中断"**：只要 subagent transcript 在 termAt 之后写了新行就判定为假终态恢复。但 `[Request interrupted by user]` 是终止性事件，不是恢复。
3. **权威终止信号未被利用**：main transcript 里有精确的 subagent 终止信号，但当前代码完全没解析。

### 已发现的权威信号（main transcript，逐行核实）

| 场景 | 信号 | 格式 | 带 agent id |
|---|---|---|---|
| 正常完成 | `<task-notification>` | `user` 消息，content 为字符串 `<task-notification><task-id>{agentId}</task-id>...<status>completed</status>...</task-notification>` | ✅ 精确 |
| 用户中断 | `agents_killed` | `system` 条目，`subtype:"agents_killed"`（无 agent id） | ⚠️ 会话级 |

历史样本：23 次 task-notification 全是 `status=completed`；中断全是 `agents_killed` 且无配对 task-notification。两者互斥。

## 修复方案

**核心思路**：用 main transcript 的权威终止信号驱动 subagent 终态，把 40s 静默阈值从"判定 Completed"降级为"兜底，且不再触发 escape hatch 误判"。

### 改动 1：TranscriptTailer 解析 subagent 终止信号

`ClaudeMonitor/Services/TranscriptTailer.cs`

**1a. `ProcessUser` 识别 `<task-notification>`**

当前 `ProcessUser` 把 string-content 的 user 消息一律当"real user message"调 `OnTranscriptUserMessage`。`<task-notification>` 正是这种形态，会被误当 user 消息触发 WaitingApi 窗口。

改为：当 `message.content` 是字符串且包含 `<task-notification>` 时，解析出 `<task-id>` 和 `<status>`，调用新方法 `_sessionManager.OnSubagentTaskNotification(sessionId, agentId, status, atUtc)`，**不**调 `OnTranscriptUserMessage`（它不是真正的用户消息）。

解析用简单的字符串/正则提取（`<task-id>...</task-id>`、`<status>...</status>`），不引入 XML 依赖。status 目前只见过 `completed`；映射为 `SubagentState.Completed`。其他 status 值保守映射为 `Completed`（task-notification 出现即表示停止）。

**1b. `ProcessSystem` 识别 `agents_killed`**

当前 `ProcessSystem` 只处理 `stop_hook_summary`。新增：`subtype == "agents_killed"` 时调用 `_sessionManager.OnSubagentsKilled(sessionId, atUtc)`。

`agents_killed` 不带 agent id，所以是会话级信号——杀掉该 session 下所有**仍在工作**的 subagent（标记为终态）。结合时序，被中断的 subagent 此刻正在工作，正常完成的 subagent 已经由 task-notification 处理过，不会被误杀。

### 改动 2：SessionManager 接入终止信号，权威标记终态

`ClaudeMonitor/Services/SessionManager.cs`

**2a. `OnSubagentTaskNotification(sessionId, agentId, status, atUtc)`**

- **先在 tailer 层标记权威终态**（`_tailer.MarkAuthoritativeTerminal(agentId)`），独立于 row 生命周期。这一步必须先做，因为 row 可能已被 40s 假终态移除（时序竞争：`16:41:34` 假终态 → `16:41:37` row 移除 → `16:42:33` 中断信号到达时 row 已不存在）。
- 然后定位 row：若 row 仍在，调用 `UpdateSubagentState(agentId, SubagentState.Completed, "", StateSource.Transcript)` 复用现有 terminal removal 路径（row 显示 Completed 3s 后消失）。若 row 已被移除，无需再做什么——权威终态标志已防止 watcher 重新激活它。
- **关键**：权威终态标志**不**受 escape hatch 影响（见改动 3），且 `DeactivateSubagent`/`RemoveSubagent` 都不清除它（只有 `ClearAllTerminal` 会话移除时清）。

**2b. `OnSubagentsKilled(sessionId, atUtc)`**

- 遍历该 session 所有 `IsWorking` 的 subagent，逐个**先**在 tailer 层标记权威终态，再 `UpdateSubagentState(..., SubagentState.Failed, ...)`（中断视为 Failed/停止）。
- 同样标记为权威终态，不受 escape hatch 影响。

### 改动 3：SubagentTailer 区分权威终态 vs 静默猜测，修复 escape hatch

`ClaudeMonitor/Services/SubagentTailer.cs` + `SubagentWatcher.cs`

**3a. 引入权威终态标志**

新增 `_authoritativeTerminal` 集合（或把 `_terminalAt` 的 value 改为结构体 `{ At, IsAuthoritative }`）。`OnSubagentTaskNotification`/`OnSubagentsKilled` 设置的终态是权威的；40s 静默推导的终态是猜测的。

**3b. escape hatch 只对"猜测终态"生效**

`SubagentWatcher.PollSession` 的 escape hatch（`lastActivity > termAt` → `ClearTerminal` + 重新激活）改为：**仅当终态是猜测的**才允许 escape。权威终态（task-notification / agents_killed）即使文件再增长也不复活——因为那一定是新的一次调用（同 task-id 可多次 notify，但每次 notify 都会重新走权威路径）。

这直接修复 Subagent 2 的闪烁：`16:42:33` 的中断不会有 task-notification，但会有 `agents_killed` → 权威终态 → escape hatch 不复活 → row 保持消失。

**3c. 静默阈值保留为兜底，但提高 + 改语义**

`IdleToCompletedSeconds` 从 40s 提到 **120s**（覆盖观察到的 99.8s 中断静默 + 余量；221s 长思考仍会触发，但那是猜测终态，escape hatch 会纠正）。更重要的是：静默推导的 Completed 现在是**猜测终态**，escape hatch 仍可纠正——所以即使 221s 触发假终态，subagent 恢复活动时会被正确救回（因为它是猜测终态，且新行是真正的 assistant 活动，不是中断）。

**3d. escape hatch 增加内容判断（双保险）**

即使对猜测终态，escape hatch 复活前先看新行内容：如果新行是 `[Request interrupted by user]`（user 消息，string content），不复活（判定为真终态）。这处理 `agents_killed` 信号丢失/滞后的边缘情况。

### 改动 4：保留并确认未提交的 `IsTailing` 守卫

`SubagentWatcher.cs` 已有的未提交修改（`IsTailing` 守卫，防止 20s fallback 杀掉已 attach 的 tail）是正确的，保留。它解决的是另一条路径（fallback 误杀 tail 导致 offset 重置丢历史，Subagent 1 的 `Pending` 现象）。本次修复与它互补。

## 不做

- 不改 `SubagentState` 枚举（Completed/Failed 已够用；中断映射 Failed）。
- 不持久化 terminal memory（重启从空开始，可接受——重启后靠 task-notification/agents_killed 重新建立）。
- 不改 main agent 状态机（只动 subagent 路径）。

## 验证

- `dotnet build` 通过。
- 手动场景：正常完成（应看到 Completed→消失，不复活）、用户中断（应看到 Failed/消失，不复活）、长思考>120s（猜测 Completed→恢复活动时 escape hatch 正确救回）。
- 日志：新增 `subagent task-notification` / `subagent agents_killed` / `subagent authoritative terminal` 日志行，便于追溯。
