# 修复 subagent 终态推导缺口 + 行常驻 UI

## 根因

两个问题同源：

1. **终态推导缺口**：`SubagentTailer.DeriveState` 依赖 `system.stop_hook_summary` 推导 `Completed`/`Failed`，但实测 Claude Code **不在 subagent 转录里写任何 `system` 条目**（转录只有 assistant/user/attachment）。所以 `_turnEnded` 永不置 true，subagent 状态永久卡在 `Thinking`。

2. **行常驻 UI**：XAML subagent 行可见性绑定 `HasSubagentActivity`（锁存字段，置 true 后永不回 false）。设计意图是"行 persists 显示 idle 过渡"，但配合问题 1，终态推不出 → 行无法干净消失 → 集合被 watcher fallback 清空后，`SubagentStatusText` 返回"subagent 空闲" → 绿色空闲行常驻。

数据约束（已验证）：
- subagent 转录无 `system`/`stop_hook_summary`，`parentUuid`/`sourceToolAssistantUUID` 均为 None → 无法从主会话 tool_result 精确关联 subagent 终态
- 主会话用 `Agent` 工具派生（非 `Task`），tool_use id (`call_xxx`) 与 subagent agentId (`ad387fb...`) 无映射
- 唯一可靠终态信号：**subagent 转录长时间无新条目 + 无 unpaired tool_use**

## 修复方案

### 改动 1：SubagentTailer — 时间-based 终态兜底

在 `DeriveState` 中，`Thinking` 分支前增加一条**静默超时推导**：若最后一次转录活动距今超过阈值、且无 unpaired tool_use、且非 WaitingApi 条件，则推导为 `Completed`。

`SubagentTailer.cs`：
- 新增常量 `IdleToCompletedSeconds = 20`（与 watcher `FallbackStaleSeconds` 对齐）
- `SubagentFileTail` 已有 `_lastEntryUtc`（每条条目更新），用它判断静默时长
- `DeriveState` 在 `Thinking` 分支前插入：
  ```
  if (_lastAssistantUtc is not null && _unpairedToolUseIds.Count == 0
      && (nowUtc - _lastEntryUtc).TotalSeconds > IdleToCompletedSeconds)
  {
      return SubagentState.Completed;
  }
  ```
  - 注意：`_lastEntryUtc` 在 `ProcessLine` 里对所有解析成功的条目更新，是"最后活动"的准确度量
  - 阈值 20s：subagent 思考间隙通常 <10s，20s 无新条目基本可判定结束；与 watcher fallback 一致，避免 tailer 与 watcher 打架
- `Failed` 无法从转录内容可靠推导（无 error 信号），保留 `_turnEndedWithError` 路径作为未来扩展，本次不补

### 改动 2：SessionManager — 终态后延迟移除行

`UpdateSubagentState` 在设置 `State = Completed/Failed` 后，启动一个 per-agent 一次性定时器（3s），到时调用 `RemoveSubagent(sessionId, agentId)` 移除行。

`SessionManager.cs`：
- 新增 `ConcurrentDictionary<string, System.Threading.Timer> _subagentRemovalTimers`（key = agentId）
- `UpdateSubagentState` 中，当 `state` 是 `Completed` 或 `Failed` 时：
  ```
  ScheduleSubagentRemoval(sessionId, agentId, TimeSpan.FromSeconds(3));
  ```
- 新增 `ScheduleSubagentRemoval`：创建一次性 Timer，回调里 `RemoveSubagent(sessionId, agentId)` 并从字典移除/释放 timer。若已存在同 agentId 的 timer，先释放旧的（防重复）
- `RemoveSubagent` 已有的清理逻辑（移除行、清 `_subagentToSession`、`DeactivateSubagent`、`SubagentActive=false`）复用，无需改
- 在 `RemoveSubagent` 和 session 移除路径里，额外清理 `_subagentRemovalTimers` 中该 agentId 的 timer（避免移除后 timer 仍触发）

### 改动 3：SessionInfo — `HasSubagentActivity` 在集合清空时回 false

让锁存可在"所有 subagent 结束"时解除，使行随终态移除而消失。

`SessionInfo.cs`：
- `HasSubagentActivity` 的 setter 改为允许回 false（移除 `private set` 的隐式约束，setter 本就是 `SetField`，已支持）
- 在 `Subagents` 集合变化的回调里（`NotifyCollectionChanged` 或现有 hook），当 `Subagents.Count == 0` 时设 `HasSubagentActivity = false`
- 需确认 `Subagents` 集合变化的监听点。当前 `SessionInfo` 构造里对 `Subagents.CollectionChanged` 有订阅（见 diff 中 line 518 附近的 `HasSubagentActivity = true`）。在该回调里增加：`if (Subagents.Count == 0) HasSubagentActivity = false`
- 保留"首次检测到 subagent 时置 true"的逻辑不变

### 改动 4：日志

`SubagentTailer.RaiseStateIfChanged` 已有日志，终态推导会自动记录 `state=Completed`。`ScheduleSubagentRemoval` 触发移除时，`RemoveSubagent` 路径已有日志覆盖（通过 `SubagentChanged` 事件）。新增一条 `FileLogger.Info` 记录延迟移除调度，便于诊断。

## 不改动

- `Failed` 推导：无可靠数据源，保留现状（`_turnEndedWithError` 路径），未来若 CC 写入 error 信号再补
- `_parentToolUseToAgent` / `RegisterParentToolUse`：已删除，不恢复（数据上不可行）
- watcher fallback stale 窗口：保留，作为 tailer 未激活时的兜底
- `WaitingApi` 阈值 10s：不变

## 验证

1. `dotnet build` 0 警告 0 错误
2. 启动 CC-Pulse，触发 subagent，观察日志：
   - 状态序列应出现 `... Thinking → Completed`（而非卡在 Thinking）
   - `Completed` 后约 3s 出现行移除（`SubagentChanged` 事件）
3. UI：subagent 行经历 `思考中…(红) → 完成(绿，约3s) → 消失`，主 agent 恢复 Busy/Idle
4. 确认无"空闲"行常驻
