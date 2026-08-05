# CC-Pulse Phase 2：Transcript JSONL Tail 接入设计文档

## 1. Phase 2 目标定位

Phase 2 的核心使命是**将 Transcript JSONL 作为状态机的 Source of Truth**，Hook 事件降级为低延迟触发器。具体达成：

| 目标 | 说明 |
| :--- | :--- |
| 校正 Hook 丢失/乱序 | Transcript 是持久化事实，可修复内存状态漂移 |
| 补充 Subagent 内部状态 | 解析子会话 transcript，获得 thinking/tool_running/waiting 等细粒度状态 |
| 长任务精确感知 | 不再依赖 Watchdog 猜测，直接观察 tool_use/tool_result 配对 |
| 异常自愈 | SessionStart(compact)/Stop 等边界事件可通过 transcript 验证 |

> **核心原则**：Hook 驱动实时性，Transcript 驱动准确性。两者冲突时，以 Transcript 为准。

---

## 2. Transcript 文件发现与监听策略

### 2.1 文件路径规范

```
# macOS / Linux
~/.claude/projects/<project-hash>/<session-id>.jsonl

# Windows
%USERPROFILE%\.claude\projects\<project-hash>\<session-id>.jsonl
```

### 2.2 文件发现机制

采用 **Hook 引导 + 目录扫描兜底** 双模式：

```
┌─────────────────────────────────────────────────┐
│           Transcript File Discovery             │
├─────────────────────────────────────────────────┤
│ 1. Hook 引导（主路径）                           │
│    PreToolUse/UserPromptSubmit 携带              │
│    transcript_path → 立即激活 tail               │
│                                                 │
│ 2. 目录扫描（兜底）                              │
│    启动时扫描 ~/.claude/projects/                │
│    仅加载 mtime < 24h 的文件                     │
│    避免历史文件全量解析                            │
│                                                 │
│ 3. FS Watcher + Polling 混合                    │
│    fs.watch 捕获新增/追加                         │
│    500ms polling 兜底（Windows 必需）             │
│    文件 rename/delete 重建 watcher               │
└─────────────────────────────────────────────────┘
```

### 2.3 Tail 读取策略

```csharp
class TranscriptTailer
{
    // 每个文件维护独立偏移量
    Dictionary<string, long> _offsets = new();
    
    void OnFileChanged(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(_offsets[path], SeekOrigin.Begin);
        
        using var reader = new StreamReader(stream);
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            if (TryParseJsonl(line, out var entry))
                ProcessEntry(entry);
        }
        
        _offsets[path] = stream.Position;
    }
}
```

> **关键细节**：必须以 `FileShare.ReadWrite` 打开，Claude Code 持有写锁时仍可读取。

---

## 3. JSONL 条目解析规范

### 3.1 核心条目类型

| type | 关键字段 | 状态推断作用 |
| :--- | :--- | :--- |
| `user` | `message.content` | 确认用户输入已落盘 |
| `assistant` | `message.content`, `message.tool_use[]` | 模型响应、工具调用意图 |
| `tool_use` | `id`, `name`, `input` | 工具开始执行（权威来源） |
| `tool_result` | `tool_use_id`, `content` | 工具执行结束（权威来源） |
| `result` | `stop_reason`, `is_error` | Turn 结束确认 |
| `summary` | - | Compact 完成标记 |

### 3.2 Subagent 关联字段

Transcript 中区分 main/subagent 的关键字段：

```json
// Main session 中的 Task 工具调用
{
  "type": "tool_use",
  "id": "toolu_main_task_001",
  "name": "Task",
  "input": { "prompt": "...", "description": "..." }
}

// Subagent session 的 transcript 条目（通过 parent_tool_use_id 关联）
{
  "type": "tool_use",
  "id": "toolu_sub_bash_001",
  "name": "Bash",
  "parent_tool_use_id": "toolu_main_task_001",  // ← 关键关联字段
  "session_id": "sub_sess_xyz"                   // ← 子会话 ID
}
```

> **注意**：不同 Claude Code 版本字段名可能有差异（`parent_tool_use_id` / `parentToolUseId` / `sidechain`）。Phase 2 启动时需实际抓取样本确认。若缺少显式关联字段，则回退到时间窗口 + Task tool_use_id 模糊匹配。

### 3.3 解析容错

```csharp
bool TryParseJsonl(string line, out TranscriptEntry entry)
{
    entry = null;
    if (string.IsNullOrWhiteSpace(line)) return false;
    
    try
    {
        entry = JsonSerializer.Deserialize<TranscriptEntry>(line, _options);
        return entry?.Type != null;
    }
    catch
    {
        // JSONL 行损坏：记录日志，跳过该行，不中断 tail
        Log.Warn($"Malformed JSONL line in {currentFile}: {line[..Math.Min(200, line.Length)]}");
        return false;
    }
}
```

---

## 4. Hook + Transcript 融合状态机

### 4.1 融合架构

```
                    ┌──────────────────────┐
                    │    State Engine      │
                    │  (per session_id)    │
                    └──────────▲───────────┘
                               │
              ┌────────────────┼────────────────┐
              │                │                 │
     ┌────────┴───────┐ ┌─────┴──────┐  ┌───────┴────────┐
     │  Hook Events   │ │ Transcript │  │  Reconciliation│
     │  (low latency) │ │  Entries   │  │  (conflict res)│
     │  快速切换       │ │ (authority)│  │  定期校验       │
     └────────────────┘ └────────────┘  └────────────────┘
```

### 4.2 融合规则优先级

```
置信度排序：
  Transcript tool_use/tool_result > Hook PreToolUse/PostToolUse
  Transcript result             > Hook Stop
  Hook UserPromptSubmit         > Transcript user (更低延迟)
  Hook Notification             > Transcript (transcript 通常不含通知)
```

### 4.3 冲突解决策略

| 冲突场景 | 处理方式 |
| :--- | :--- |
| Hook 说 Busy，Transcript 显示 tool_result 已完成且 result 已写入 | 以 Transcript 为准 → Idle |
| Hook 说 Idle(Stop)，Transcript 仍有未完成 tool_use | 以 Transcript 为准 → Busy + 记录 anomaly |
| Hook PreToolUse 到达，Transcript 尚未写入对应 tool_use | 信任 Hook（transcript 有写入延迟），设置 grace_period=2s |
| Transcript 出现 tool_use，但从未收到 Hook PreToolUse | 以 Transcript 为准补状态 → Busy + 记录 hook_missed |
| Grace period 内 Transcript 确认了 Hook 事件 | 正常流转，清除 grace marker |
| Grace period 超时 Transcript 未确认 | 保留 Hook 状态但标记 unconfirmed |

### 4.4 修订后的状态判定逻辑

```csharp
MainState DeriveMainState(SessionState s)
{
    // Transcript 是最终仲裁者
    if (s.TranscriptHasUnpairedToolUse)
        return MainState.Busy;
    
    if (s.TranscriptHasActiveSubagent)
        return MainState.Busy;
    
    if (s.TranscriptLastEntry is ResultEntry r && r.StopReason != null)
        return MainState.Idle;
    
    // Transcript 无明确结论时，参考 Hook 状态（带 grace period）
    if (s.HookState == MainState.Busy && s.WithinGracePeriod)
        return MainState.Busy;
    
    if (s.HookState == MainState.Idle)
        return MainState.Idle;
    
    // 兜底
    return s.LastKnownState;
}
```

---

## 5. Subagent 内部状态补充

### 5.1 Subagent 状态枚举

```csharp
enum SubagentState
{
    Pending,        // Task tool_use 已出现，子会话 transcript 尚未创建
    Thinking,       // 子会话最后条目为 user/assistant 文本，无未完成 tool_use
    ToolRunning,    // 子会话存在未配对 tool_use
    WaitingApi,     // 子会话 user 消息后长时间无 assistant 响应
    Completed,      // 子会话出现 result 或 main 收到对应 tool_result
    Failed          // 子会话 result.is_error == true
}
```

### 5.2 Subagent 状态推导

```csharp
SubagentState DeriveSubagentState(SubagentSession sub)
{
    if (sub.TranscriptPath == null || !File.Exists(sub.TranscriptPath))
        return SubagentState.Pending;
    
    var lastEntries = sub.GetRecentEntries(count: 5);
    
    // 检查未配对 tool_use
    if (sub.HasUnpairedToolUse)
        return SubagentState.ToolRunning;
    
    // 检查是否结束
    var lastResult = lastEntries.OfType<ResultEntry>().LastOrDefault();
    if (lastResult != null)
        return lastResult.IsError ? SubagentState.Failed : SubagentState.Completed;
    
    // 检查是否在等待 API（user 消息后 > 10s 无 assistant）
    var lastUser = lastEntries.OfType<UserEntry>().LastOrDefault();
    if (lastUser != null && 
        DateTime.UtcNow - lastUser.Timestamp > TimeSpan.FromSeconds(10) &&
        !lastEntries.Any(e => e.Timestamp > lastUser.Timestamp && e is AssistantEntry))
        return SubagentState.WaitingApi;
    
    return SubagentState.Thinking;
}
```

### 5.3 Main ↔ Subagent 关联表

```csharp
class SessionRegistry
{
    // main_session_id → { parent_tool_use_id → subagent_session_info }
    Dictionary<string, Dictionary<string, SubagentSession>> _subagentMap = new();
    
    void LinkSubagent(string mainSessionId, string parentToolUseId, string subSessionId, string transcriptPath)
    {
        _subagentMap[mainSessionId][parentToolUseId] = new SubagentSession
        {
            SessionId = subSessionId,
            TranscriptPath = transcriptPath,
            ParentToolUseId = parentToolUseId,
            LinkedAt = DateTime.UtcNow
        };
        
        // 立即启动子 transcript tail
        _tailer.ActivateFile(transcriptPath);
    }
}
```

---

## 6. 输出数据结构（Phase 2）

```json
{
  "sessions": [
    {
      "session_id": "main_sess_abc",
      "project_path": "/home/user/my-project",
      "main_agent": {
        "state": "BUSY",
        "source": "transcript",
        "confidence": 0.98,
        "active_tool": {
          "tool_use_id": "toolu_123",
          "tool_name": "Bash",
          "started_at": "2026-08-04T10:00:00Z"
        }
      },
      "subagents": [
        {
          "parent_tool_use_id": "toolu_task_999",
          "session_id": "sub_sess_xyz",
          "state": "TOOL_RUNNING",
          "tool_name": "Read",
          "source": "transcript",
          "confidence": 0.95
        }
      ],
      "anomalies": [],
      "last_activity": "2026-08-04T10:00:05Z"
    }
  ]
}
```

新增字段说明：

| 字段 | 含义 |
| :--- | :--- |
| `source` | 状态判定来源：`hook` / `transcript` / `reconciled` |
| `confidence` | 置信度 0-1，transcript 确认 > 0.9，仅 hook 未确认 ≈ 0.7 |
| `anomalies` | 检测到的不一致列表，供调试和准确率统计 |

---

## 7. 性能与资源控制

| 约束 | 措施 |
| :--- | :--- |
| 避免全量扫描历史文件 | 仅加载 mtime < 24h 的文件；Hook 引导优先 |
| 大文件 tail 内存控制 | 流式逐行读取，不加载全文；单行 > 1MB 跳过并告警 |
| FS Watcher 风暴防护 | 100ms debounce；同一文件连续变更合并处理 |
| 子 transcript 数量上限 | 单 main session 最多同时 tail 10 个子文件；超限 LRU 淘汰 |
| 偏移量持久化 | 定期写入 `%LOCALAPPDATA%\CC-Pulse\tail-offsets.json`，重启续读 |
| CPU 占用 | Polling 间隔 ≥ 500ms；空闲 session 降频至 5s |

---

## 8. 实施步骤

### Step 1：Transcript 采样分析（1-2 天）

手动触发各类场景，收集 JSONL 样本，确认：
- 实际字段名（parent_tool_use_id vs parentToolUseId vs sidechain）
- Subagent transcript 文件命名规律
- Compact 后 transcript 是否截断/新建
- tool_use / tool_result 配对完整性

### Step 2：TranscriptTailer 实现（2-3 天）

- 文件发现 + FS Watcher + Polling
- 流式 JSONL 解析
- 偏移量管理 + 持久化
- 单元测试覆盖：正常追加、文件轮转、损坏行、并发写入

### Step 3：SessionRegistry + Subagent 关联（2 天）

- Main/Subagent 映射表
- 子 transcript 自动激活
- 关联字段缺失时的模糊匹配兜底

### Step 4：融合状态机替换（2-3 天）

- 实现 grace period 机制
- 冲突解决规则
- Anomaly 记录
- 替换原有纯 Hook 状态机

### Step 5：回放测试框架（1-2 天）

- 录制真实 session 的 Hook 事件流 + Transcript 文件
- 离线回放，对比 Phase 1 vs Phase 2 状态序列
- 计算 precision/recall/F1

### Step 6：灰度验证（3-5 天）

- 内部使用，收集 anomaly 日志
- 重点关注：compact 边界、Ctrl+C 中断、超长工具、嵌套 subagent

---

## 9. 风险与缓解

| 风险 | 影响 | 缓解措施 |
| :--- | :--- | :--- |
| Transcript 写入延迟导致短暂不一致 | 状态闪烁 | Grace period 2s；UI 层 debounce 300ms |
| Claude Code 更新改变 JSONL 格式 | 解析失败 | 版本探测 + 多格式适配器；解析失败不崩溃，降级为 Hook-only |
| Subagent 关联字段缺失 | 无法追踪子状态 | 时间窗口模糊匹配 + 日志告警；标记 confidence=0.5 |
| 大量并发 subagent 导致 tail 过载 | CPU/IO 飙升 | LRU 限制 + 动态降频 + 指标监控 |
| Transcript 文件被外部清理 | Tail 中断 | FileNotFoundError 优雅处理；下次 Hook 触发时重新发现 |

---

## 10. Phase 2 完成标准

- [ ] Hook 丢失场景下，状态在 Transcript 写入后 ≤ 3s 内自动修正
- [ ] Subagent 内部状态（Thinking/ToolRunning/Completed）可正确识别
- [ ] 长工具执行（> 5min）不再被 Watchdog 误判为 Idle
- [ ] Anomaly 日志可追溯每次状态修正的原因
- [ ] 回放测试 F1 ≥ 0.95（对比人工标注 ground truth）
- [ ] CPU 占用 < 5%（idle），< 15%（活跃多 subagent）
- [ ] Claude Code 版本升级后 JSONL 格式变更可在 1 天内适配

---

> **文档版本**：v2.0  
> **前置依赖**：Phase 1 Main Agent Hook 监控（已完成评审）  
> **后续衔接**：Phase 3 Notification 分类 + Phase 4 OTel/API Proxy