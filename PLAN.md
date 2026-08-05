# Phase 2 MVP 实施计划：Transcript JSONL Tail + Main Agent 状态融合

## 范围

MVP：实现 TranscriptTailer + main agent 状态融合 + anomaly 记录。**Subagent 内部状态（Thinking/ToolRunning/Completed）留到下一轮**，现有 SubagentWatcher 文件扫描保持不变。

## 采样确认的关键事实（与 TASKS.md 假设的差异）

1. **无顶层 `result` 条目**：当前 CC 版本不写 `type:"result"`。Turn 结束的权威信号是 `type:"system", subtype:"stop_hook_summary"`（含 `stopReason`、`hasOutput`、`preventedContinuation`）。
2. **`parent_tool_use_id` 字段不存在**：subagent 关联通过 `subagents/agent-<id>.meta.json` 的 `toolUseId` 字段。本轮不做 subagent 关联，不影响。
3. **`tool_use` 在 `assistant.message.content[]`**（`{type:"tool_use", id, name, input}`），**`tool_result` 在 `user.message.content[]`**（`{type:"tool_result", tool_use_id, content}`）。配对：`tool_use.id` ↔ `tool_result.tool_use_id`。
4. **Transcript 路径**：`~/.claude/projects/<encoded-project>/<session-id>.jsonl`，encoded = 把 `:\/` 替换为 `-`（SubagentWatcher.EncodeProjectPath 已实现，复用）。Hook payload 不含 `transcript_path`，但含 `session_id` + `cwd`，足够推导。
5. **顶层每行有 `timestamp`**（ISO 8601 UTC）、`type`、`sessionId`、`isSidechain`、`agentId`。

## 架构

```
                    ┌──────────────────────┐
                    │   SessionManager     │  (现有，扩展)
                    │  per session state   │
                    └──────────▲───────────┘
                               │
              ┌────────────────┼────────────────┐
              │                │                 │
     ┌────────┴───────┐ ┌─────┴──────┐  ┌───────┴────────┐
     │  HookServer    │ │ Transcript │  │  Reconciler    │
     │  (低延迟触发)   │ │  Tailer    │  │  (冲突解决)    │
     │  现有，不变     │ │ (权威)     │  │  新增          │
     └────────────────┘ └────────────┘  └────────────────┘
```

## 新增/修改文件

### 1. `ClaudeMonitor/Services/TranscriptTailer.cs`（新增）

增量 tail：每个 transcript 文件维护 offset，FS Watcher + 500ms polling 兜底。

**职责：**
- `ActivateFile(sessionId, projectPath)`：推导 transcript 路径，初始化 offset（首次定位到文件末尾，只读新增行——避免回放历史），启动监听。
- `DeactivateFile(sessionId)`：停止监听，丢弃 offset。
- 文件变更时从 offset 读取新增行，逐行解析，调用 `ProcessEntry`。
- `FileShare.ReadWrite | FileShare.Delete` 打开（CC 持有写锁时仍可读）。
- 单行 > 1MB 跳过并告警（§7）。
- FS Watcher 100ms debounce；polling 500ms 兜底（Windows 必需）。
- 文件 rename/delete 优雅处理。

**解析的条目类型（仅 main agent 相关）：**
- `assistant` + `message.content[].type=="tool_use"` → `OnToolUse(id, name)`
- `user` + `message.content[].type=="tool_result"` → `OnToolResult(tool_use_id)`
- `system` + `subtype=="stop_hook_summary"` → `OnTurnEnd(stopReason)`（权威 Idle 信号）

**输出：** 调用 `SessionManager` 的新融合方法（见下）。

### 2. `ClaudeMonitor/Services/TranscriptReconciler.cs`（新增）

融合状态机核心。定期（每 2s）+ 事件驱动校验 main agent 状态。

**融合规则（§4.2/4.3）：**
- Transcript `tool_use` 未配对 → Busy（权威）
- Transcript `stop_hook_summary` → Idle（权威，清除未配对 tool_use 并记 anomaly if any）
- Hook PreToolUse 到达但 Transcript 尚无对应 tool_use → 信任 Hook，grace_period=2s
- Grace period 内 Transcript 确认 → 清除 grace marker
- Grace period 超时未确认 → 保留 Hook 状态，标记 unconfirmed（confidence=0.7）

**DeriveMainState 逻辑（§4.4 修订）：**
```csharp
MainState DeriveMainState(SessionState s)
{
    if (s.TranscriptHasUnpairedToolUse) return Busy;       // 权威
    if (s.TranscriptLastSystemStop) return Idle;            // 权威
    if (s.HookState == Busy && s.WithinGracePeriod) return Busy;
    if (s.HookState == Idle) return Idle;
    return s.LastKnownState;
}
```

### 3. `ClaudeMonitor/Models/SessionInfo.cs`（修改）

新增 transcript 状态字段（与现有 Hook 字段并存）：
- `TranscriptActiveTools`：`HashSet<string>` — transcript 观察到的未配对 tool_use_id
- `TranscriptLastStopSeen`：`DateTime?` — 最后一次 stop_hook_summary 时间
- `HookState`：`SessionStatus` — 当前 Hook 路径设置的状态（现有 `Status` 改名或新增）
- `StateSource`：`enum { Hook, Transcript, Reconciled }` — 状态判定来源
- `Anomalies`：`List<AnomalyRecord>` — 近期 anomaly（供调试/输出）

**关键：** `Status`（UI 绑定）改为由 Reconciler 推导，而非 Hook 直接写。Hook 路径写 `HookState`，Transcript 路径写 transcript 字段，Reconciler 合成最终 `Status`。

### 4. `ClaudeMonitor/Services/SessionManager.cs`（修改）

新增方法：
- `OnTranscriptToolUse(sessionId, toolUseId, toolName)` — transcript 观察到 tool_use
- `OnTranscriptToolResult(sessionId, toolUseId)` — transcript 观察到 tool_result
- `OnTranscriptTurnEnd(sessionId, stopReason)` — transcript 观察到 stop_hook_summary
- `Reconcile(sessionId)` — 运行 DeriveMainState，更新 `Status`，记录 anomaly

现有 `UpdateStatus` 改为更新 `HookState` 并触发 reconcile（而非直接写 `Status`）。

### 5. `ClaudeMonitor/App.xaml.cs`（修改）

启动时：
- 创建 `TranscriptTailer`，注入 `SessionManager`
- `AddSession` 时调用 `tailer.ActivateFile(sessionId, projectPath)`
- `RemoveSession` 时 `tailer.DeactivateFile(sessionId)`
- 启动 Reconciler 定时器（2s）

### 6. `ClaudeMonitor/Models/AnomalyRecord.cs`（新增）

```csharp
record AnomalyRecord(string Type, DateTime At, string Detail);
// 类型: stop_with_unpaired_tool_use, hook_missed, grace_expired, transcript_hook_conflict
```

## 实施步骤

### Step 1：TranscriptTailer 基础（增量 offset + 解析）
- 新建 `TranscriptTailer.cs`
- 路径推导（复用 EncodeProjectPath 逻辑，提到共享位置）
- 增量 tail + FS Watcher + polling
- JSONL 解析（容错：损坏行跳过不中断）
- 单元测试：正常追加、文件轮转、损坏行、并发写入

### Step 2：SessionInfo 扩展 transcript 字段
- 新增 `TranscriptActiveTools`、`TranscriptLastStopSeen`、`HookState`、`StateSource`、`Anomalies`
- 保持 UI 绑定的 `Status` / `IsWorking` 兼容

### Step 3：SessionManager 融合方法 + Reconciler
- `OnTranscriptToolUse/ToolResult/TurnEnd`
- `Reconcile(sessionId)` + DeriveMainState
- grace period 机制
- anomaly 记录
- 现有 Hook 路径改为写 `HookState` + 触发 reconcile

### Step 4：App.xaml.cs 接线
- TranscriptTailer 生命周期管理
- AddSession/RemoveSession 集成
- Reconciler 定时器

### Step 5：验证
- `dotnet build` 通过
- 手动场景测试：正常 turn、长工具、Ctrl+C 中断、compact
- anomaly 日志可追溯

## 不做（下一轮）

- Subagent 内部状态（Thinking/ToolRunning/WaitingApi/Completed）
- Subagent transcript tail
- 偏移量持久化到磁盘（§7，本轮用内存 offset，重启从文件末尾开始）
- 回放测试框架（Step 5）
- 灰度验证（Step 6）

## 风险

- **无 result 条目**：用 `system.stop_hook_summary` 替代，已采样确认存在。
- **Transcript 写入延迟**：grace period 2s + UI debounce 300ms（现有）。
- **状态机改动风险**：保持 Hook 路径作为 fallback，Transcript 解析失败时降级为 Hook-only（不崩溃）。
