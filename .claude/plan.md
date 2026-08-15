# 修复 main-fine WaitingApi 缺陷 + subagent 假终态重现

## 缺陷 1：WaitingApi 永不触发

### 根因
`TranscriptTailer.ProcessUser`（第 481-508 行）在 `message.content` 不是数组时直接 return：
```csharp
if (!msg.TryGetProperty("content", out var content) ||
    content.ValueKind != JsonValueKind.Array) return;  // ← 字符串 content 直接 return
```
但真正的用户输入消息 `message.content` 是**字符串**（如 `"你可以启动一个子代理吗"`），不是数组。字符串 content 不满足 `Array` 条件，整个方法提前 return，`OnTranscriptUserMessage` 从未被调用，`LastUserMessageUtc` 始终为 null，`WaitingApi` 状态永远无法派生。

日志佐证：所有 `main-fine` 行都是 `lastUser=null`。

### 修复
`ProcessUser` 重构为：先提取 timestamp；若 content 是数组，走 tool_result 配对逻辑并记录是否含 tool_result；若 content 不是数组（字符串等），视为纯 user 消息（必然不含 tool_result）。**无论 content 是否数组**，只要不含 tool_result，就调用 `OnTranscriptUserMessage`。

`TranscriptTailer.cs` `ProcessUser`：
```csharp
private void ProcessUser(JsonElement root)
{
    // timestamp 在顶层 root，无论 content 结构如何都可提取
    var atUtc = ExtractTimestamp(root);

    if (!root.TryGetProperty("message", out var msg) ||
        msg.ValueKind != JsonValueKind.Object)
    {
        // 无 message 字段仍可能是 user 消息，按纯 user 消息处理
        _sessionManager.OnTranscriptUserMessage(_sessionId, atUtc);
        return;
    }

    bool hadToolResult = false;
    if (msg.TryGetProperty("content", out var content) &&
        content.ValueKind == JsonValueKind.Array)
    {
        // 数组 content：配对 tool_result
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("type", out var t) || t.GetString() != "tool_result") continue;
            hadToolResult = true;
            if (!item.TryGetProperty("tool_use_id", out var idProp)) continue;
            var id = idProp.GetString();
            if (string.IsNullOrEmpty(id)) continue;
            _sessionManager.OnTranscriptToolResult(_sessionId, id);
        }
    }
    // 字符串/其他 content：hadToolResult 保持 false → 视为纯 user 消息

    // 纯 user 消息（无 tool_result）启动 WaitingApi 窗口
    if (!hadToolResult)
    {
        _sessionManager.OnTranscriptUserMessage(_sessionId, atUtc);
    }
}
```

注意：`ExtractTimestamp` 提前到配对逻辑之前，避免重复调用。原代码在 `!hadToolResult` 分支内调用 `ExtractTimestamp(root)`，重构后统一在开头提取。

## 缺陷 2：subagent 假终态后重现（"消失→重新启动中"闪烁）

### 根因链
1. subagent 有较长思考/执行间隙（实测 32s：`23:30:45 → 23:31:17`）
2. tailer `IdleToCompletedSeconds = 20` 阈值不够，20s 无新条目误判 `Completed`（`23:31:05`）
3. 3s 后 `RemoveSubagent` 移除行 + `DeactivateSubagent` 停 tailer（`23:31:08`）
4. **subagent 实际还在运行**，转录文件继续写入（`23:31:17` 有新条目）
5. watcher `PollSession`（2s 间隔）重新扫描，`ReadLastActivityUtc` 读到文件最后行 timestamp 距今仅 1s < 20s，重新激活 tailer + `UpdateSubagents` 添加新行（`23:31:18`）
6. 新行 `State` 默认 `Pending`（启动中），tailer 重新激活时 offset=EOF 无历史，`DeriveState` 返回 `Pending`，直到新行到达才更新 → UI 显示"重新启动中"

**本质**：tailer（静默超时判终态）与 watcher（文件活跃判存活）两个独立系统对"终态"判断不一致。tailer 说"结束"移除行，watcher 说"还活着"加回行。

### 数据约束（已验证）
- `SubagentStop` hook **未配置且不可靠**（代码多处注释强调），不能作终态信号
- 主会话 Agent 工具的 tool_result 在 subagent **启动时**就配对（`23:30:15`，subagent `23:30:16` 才 Pending），**不是结束信号**
- meta.json 无 end 时间戳，只有 `toolUseId`（关联主会话 Agent tool_use，但该 tool_result 非终态）
- 唯一能区分"长间隙"与"真结束"的信号：**subagent 转录文件是否继续增长**。但长间隙时文件暂时不增长，无法在静默时刻区分

### 修复方案：终态记忆 + 文件增长逃生阀

核心思路：tailer 判定终态后，把 agentId 加入"已知终态"集合；watcher 扫描时跳过该 agentId（不重新激活、不重新加行）。**但保留逃生阀**：如果该 subagent 的转录文件在终态判定后**确实又增长了**（新行写入），说明是假终态，清除记忆允许重新激活。

这避免了"黑名单永久挡住真还在运行的 subagent"的风险：假终态后 subagent 继续写文件 → 文件增长 → 逃生阀触发 → 重新激活并从新行派生状态（不再卡 Pending，因为新行会立即被 tailer 处理）。

#### 改动 1：SubagentTailer — 终态记忆

`SubagentTailer.cs`：
- 新增 `ConcurrentDictionary<string, DateTime> _terminalAt`（key=agentId, value=终态判定时刻）
- `RaiseStateIfChanged` 中，当派生出 `Completed`/`Failed` 时，记录 `_terminalAt[agentId] = nowUtc`
- `DeactivateSubagent` 不清除 `_terminalAt`（终态记忆独立于 tail 生命周期）
- 新增 `IsTerminal(agentId)` → 是否在终态记忆中
- 新增 `ClearTerminal(agentId)` → 清除记忆（逃生阀用）
- 新增 `HasGrownSince(agentId, jsonlPath, sinceUtc)` → 读取文件最后行 timestamp，若 > sinceUtc 返回 true（文件在终态后增长）
  - 复用 watcher 的 `ReadLastActivityUtc` 逻辑（提取最后行 timestamp）。为避免重复代码，可将该方法提取为 `SubagentTailer` 的静态方法，或 watcher 调用 tailer 的方法
  - 简化：直接在 watcher 里用现有 `ReadLastActivityUtc` 判断

#### 改动 2：SubagentWatcher — 跳过终态 agent，带逃生阀

`SubagentWatcher.cs` `PollSession`（第 157-192 行）的 `foreach` 循环内，在 `ReadLastActivityUtc` stale 检查之后、`ActivateSubagent` 之前，插入：
```csharp
// 跳过已被 tailer 判定终态的 subagent，避免假终态后重新激活闪烁。
// 逃生阀：若文件在终态判定后确实增长（新行），说明是假终态，清除记忆并重新激活。
if (_tailer.IsTerminal(agentId))
{
    if (lastActivity is not null && _tailer.TryGetTerminalAt(agentId, out var termAt)
        && lastActivity.Value > termAt)
    {
        // 文件在终态后增长 → 假终态，清除记忆重新激活
        _tailer.ClearTerminal(agentId);
        FileLogger.Info($"subagent terminal override (file grew) agent={agentId} termAt={termAt:o} lastActivity={lastActivity:o}");
    }
    else
    {
        // 真终态或文件未增长 → 跳过，不重新激活/加行
        _tailer.DeactivateSubagent(agentId);
        continue;
    }
}
```
- `lastActivity` 已在上方提取（`ReadLastActivityUtc`），复用
- 逃生阀用文件最后行 timestamp > 终态时刻判断"是否增长"，无需额外 IO

#### 改动 3：SessionManager — RemoveSubagent 清理时机

`RemoveSubagent` 已调用 `DeactivateSubagent`（停 tailer），**不清除** `_terminalAt`（保留终态记忆，阻止 watcher 重新加行）。这是关键：终态记忆的生命周期长于 tail 生命周期。

session 移除路径（`RemoveSession`）里清除该 session 所有 subagent 的终态记忆（避免跨 session 泄漏）。`SubagentTailer` 新增 `ClearAllTerminal()` 或按 agentId 清除。

#### 改动 4：阈值调整（可选，建议）

`IdleToCompletedSeconds` 20s → **40s**。实测 32s 间隙触发假终态，40s 可覆盖大多数思考间隙。配合终态记忆+逃生阀，即使 40s 仍误判，闪烁也不再发生（行不会被 watcher 加回）。阈值提高让真终态的行多停留至 40s+3s 才消失，但终态记忆阻止 watcher 干扰，体验可接受。

**注意**：阈值调整是次要优化，核心修复是终态记忆+逃生阀。若不想改阈值，保留 20s 也可——闪烁由终态记忆消除，只是假终态时行会先消失（3s 后）再由逃生阀在文件增长时重新出现。为减少这种"先消失再出现"，建议提高阈值到 40s。

## 验证

1. `dotnet build` 0 警告 0 错误
2. 缺陷 1：触发 main agent，用户输入后等待 >10s 无响应 → 日志应出现 `main-fine ... WaitingApi ... lastUser=<timestamp>`（而非 `lastUser=null`）
3. 缺陷 2：触发长时间 subagent（含 >20s/40s 思考间隙）：
   - 若 tailer 误判 Completed → 行移除 → 但 watcher **不重新加行**（日志无 `subagent state ... Pending` 重现）
   - 若文件在终态后增长 → 日志出现 `subagent terminal override (file grew)`，行重新出现并从新行派生正确状态（非卡 Pending）
4. 正常 subagent（短间隙）→ Completed → 3s 后行消失，无重现
