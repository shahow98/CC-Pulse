# Hook 通道失效排查计划

## 背景：日志暴露的现象

`~/.cc-pulse/logs/` 下 4 天日志（约 3200 行）显示融合状态机异常刷屏：

| 异常类型 | 08-06 | 08-07 | 08-08 | 三天合计 |
|---|---|---|---|---|
| `hook_missed` | — | — | 52 | **656** |
| `grace_expired` | — | — | 37 | **274** |

08-08 当天：transcript 观察到 **106 个 tool_use**，但 `hookTracked=False` 占 **105/106**；hook 状态转换日志只有 9 行（全是粗粒度 `Idle↔Busy`，无 PreToolUse/PostToolUse 配对痕迹）；reconcile 仅触发 1 次。

**表面结论**：Hook 通道似乎完全失效，Transcript 单兵支撑状态机。

## 排查已完成的静态分析

代码与配置链路已全部读过，**注册与转发代码本身是健全的**：

1. **`~/.claude/settings.json`（实际生效）配置正确** — 9 个事件齐全（SessionStart/PreToolUse/PostToolUse/UserPromptSubmit/Notification/Stop/StopFailure/SubagentStop/SessionEnd），全部用 exec form `C:/Program Files/CC-Pulse/CC-Pulse-Hook.exe` + `args`，PostToolUse→`busy`（非旧版 `idle`）。`CC-Pulse-Hook.exe` 存在（160KB）。
2. **HookServer（`localhost:8765`）在监听**，`HandleRoute` 能按 `hookEvent` 正确分发 PreToolUse→`TrackTool`+`UpdateStatus(Busy)`、PostToolUse→`UntrackTool`。
3. **HookProxy/HookRunner 转发逻辑完整** — 读 stdin JSON，提取 `hook_event_name`/`tool_use_id`，POST 到 HookServer，失败入队 `~/.claude/cc-pulse-queue.ndjson`（该队列文件不存在 → 说明没有投递失败）。
4. **手动触发 `CC-Pulse-Hook.exe busy` 退出码 0** → POST 成功，链路通。

### 静态分析发现的关键线索

**线索 A — 日志"hook 只有 9 行"是假象，不是 hook 没到达。**
`SessionManager.UpdateStatus` 第 172 行：当 `oldStatus==newStatus && HookState==newStatus` 时 early-return，跳过 `FileLogger.Info`。即同一 turn 内后续 PreToolUse（`Busy->Busy`）**不打日志**。所以"9 行 hook"只反映状态翻转次数，不代表 hook 到达数。真正的问题在 transcript 侧的 `hookTracked=False`。

**线索 B — `hook_missed` 判定基于 tool_use_id 集合匹配，而 id 格式可疑。**
`OnTranscriptToolUse`（SessionManager.cs:621）调用 `IsToolHookTracked(toolUseId)`：检查 transcript 报上来的 id 是否在 hook 路径的 `ActiveTools` 集合里。集合由 PreToolUse hook 的 `tool_use_id` 填充（`TrackTool`）。

实测 transcript 里的 tool_use id 是 **DeepSeek API 格式**：`call_xxx` 和 `chatcmpl-tool-xxx`（非 Anthropic 官方 `toolu_xxx`）。环境确认：`~/.claude/settings.json` 的 `ANTHROPIC_BASE_URL=https://api.deepseek.com/anthropic`，模型全映射到 `deepseek-v4-flash`。

**核心待验证假设**：Claude Code 在第三方 API（DeepSeek）下，PreToolUse hook payload 的 `tool_use_id` 字段——
- 可能携带 Anthropic 内部生成的 `toolu_xxx`（与 transcript 的 `call_xxx` 不匹配）；
- 可能根本不填 `tool_use_id`（`TrackTool` 第 324 行 `if (string.IsNullOrEmpty(toolUseId)) return` → 集合永远空）；
- 可能携带与 transcript 一致的 `call_xxx`（那问题在别处，如时序）。

任一情况都会导致 `IsToolHookTracked` 永远 false → `hook_missed` 刷屏 → `grace_expired` 跟着刷（hook 报 Busy 但 transcript 的 tool_use 没被 hook"确认"）。

**线索 C — `grace_expired` 是 `hook_missed` 的下游症状。**
hook 报 Busy → 启动 grace period 等 transcript 确认 → transcript 的 tool_use 因 id 不匹配未被 hook 确认 → grace 超时。修好线索 B，C 自动消失。

## Step 1/2 实测结论（2026-08-08 完成）

### 方法
在 `HookServer.HandleRequestAsync` 入口加临时调试日志（`~/.cc-pulse/logs/hook-payload-debug.log`），记录每个到达 hook 的 `route`/`hookEvent`/`toolUseId`/`toolName`/raw body。重新编译发布到临时目录，停旧进程、启诊断版，正常使用 Claude Code 触发工具调用，对比 payload 与 transcript。

### Step 1 判定：第三分支（id 一致，根因在时序）

payload debug log 实证（诊断版运行期间 23 条 hook）：

```
18:38:21.171  PreToolUse  Grep  id=chatcmpl-tool-9f2df41a1ebd914f
18:38:21.544  PostToolUse Grep  id=chatcmpl-tool-9f2df41a1ebd914f   ← Pre/Post 间隔 373ms
18:38:25.167  PreToolUse  Read  id=chatcmpl-tool-8a65c86698e5d066
18:38:25.363  PostToolUse Read  id=chatcmpl-tool-8a65c86698e5d066   ← 间隔 196ms
18:39:22.971  PreToolUse  Read  id=call_614175a1508b4affaae67b12
18:39:23.174  PostToolUse Read  id=call_614175a1508b4affaae67b12   ← 间隔 203ms
```

- **hook payload 的 `toolUseId` 与 transcript tool_use id 完全一致**（同为 DeepSeek 格式 `chatcmpl-tool-xxx` / `call_xxx`，已用 `grep` 确认 id 出现在当前会话 transcript JSONL 中）。
- **每个 PreToolUse 都有对应 PostToolUse，配对完整**——hook 通道完全正常，无丢失。
- **id 体系不一致 / hook 不携带 tool_use_id / hook 不触发** 三个假设全部排除。

### Step 2 判定：hook 全序列正常触发

payload log 含完整 `PreToolUse`+`PostToolUse` 配对（Bash/Grep/Read/codegraph 均覆盖）。非"Claude Code+DeepSeek 不触发工具级 hook"。

### 根因：hook 系统性滞后于 transcript 落盘（时序倒挂）

旧日志 14:12:33 一组的完整时序：

```
14:12:33.261  transcript tool_use  id=chatcmpl-tool-8438e76cdc2f2649  hookTracked=False   ← transcript 先到
14:12:33.262  hook_missed anomaly
14:12:33.345  hook Busy->Busy activeOps=True (PreToolUse 到达)                          ← hook 晚 84ms
14:12:33.852  transcript tool_result id=chatcmpl-tool-8438e76cdc2f2649 unpaired=False    ← 591ms 后 tool_result
```

**机制**：PreToolUse hook 经 `CC-Pulse-Hook.exe` 进程冷启动 + HTTP 往返（~80-200ms）才到 HookServer；transcript JSONL 由 Claude Code 主进程直接写盘，FSWatcher/500ms 轮询几乎实时发现。所以 **transcript tool_use 总是先于 PreToolUse hook 到达**。

`OnTranscriptToolUse`（SessionManager.cs:621）在 transcript 看到 tool_use 时**立即**检查 hook 的 `_activeTools` 是否含该 id——此时 PreToolUse 尚未到达 → `IsToolHookTracked` 返回 false → `hook_missed` 误报。

这是**设计缺陷**：用慢通道（hook）的实时状态校验快通道（transcript）刚观察到的事件，必然频繁误报。08-08 数据：`hookTracked=False` 90 次 / `True` 仅 3 次（True 是偶发 hook 赢了 transcript）。

### `grace_expired` 根因（下游症状，但机制略不同）

`grace_expired`（SessionManager.cs:761）判定条件：hook 处于 Busy 且 grace period 过期，且 `!HasActiveOperations`（hook 的 `_activeTools` 为空）。

快工具（Bash ~600ms）的 PreToolUse→PostToolUse 整周期短于 grace 检查节奏。到 reconcile 时：PostToolUse 已把 hook `_activeTools` 清空，transcript tool_result 也把 `_transcriptActiveTools` 清空，但 hook `HookState` 仍是 Busy（Stop hook 未到，turn 未结束）。`DeriveMainState` 走到规则 4 → `grace_expired`。这其实是"工具刚结束、turn 还在进行"的正常状态，被误判为异常。

### 排查后清理
- 诊断版已停，正式版（`C:\Program Files\CC-Pulse\ClaudeMonitor.exe`）已重启。
- HookServer.cs 的 TEMP DEBUG 块（第 105-118 行）**仍在源码中，未 commit**，需在修复后移除。
- `hook-payload-debug.log` 可保留作证据，修复后删除。

## Step 3：修复方案（已实施 — 方向 b）

**选定方向**：b（放宽 `hook_missed` 判定，引入 hook 到达宽限期）。

### 已完成的代码改动

**`SessionInfo.cs`** — 新增"待确认"机制：
- `_pendingHookConfirms` 字典（id → 观察时间）+ `_pendingHookConfirmsLock`，记录 transcript 已看到但 PreToolUse hook 尚未到达的 tool_use id。
- `PendingHookConfirmTimeoutMs = 500`（覆盖实测 ~200ms 滞后 + 余量）。
- `HasPendingHookConfirm` 属性：宽限期内有待确认项时为 true，用于抑制 `grace_expired`。
- `MarkPendingHookConfirm` / `ClearPendingHookConfirm` / `DrainExpiredPendingHookConfirms` / `ClearPendingHookConfirms`。
- `TrackTool`（PreToolUse 到达）调用 `ClearPendingHookConfirm` 清除对应待确认项。
- `ClearTranscriptTools` / `ClearActiveTools`（turn end / reset）顺带清空待确认集合。
- `_hookBusyConfirmedByTranscript` 标志 + `MarkHookBusyConfirmedByTranscript` / `ResetHookBusyConfirmed`：transcript 看到 tool_use 即标记当前 hook Busy 已确认，抑制后续 between-tools gap 的 `grace_expired`。

**`SessionManager.cs`** — 改判定逻辑：
- `OnTranscriptToolUse`：不再立即判 `hook_missed`，改为 `MarkPendingHookConfirm` 记录待确认 + `MarkHookBusyConfirmedByTranscript` 标记确认。
- `Reconcile`：每 tick 调 `DrainExpiredPendingHookConfirms`，仅超时未确认的才记 `hook_missed`（真异常）。
- `DeriveMainState` 规则 4（`grace_expired`）：增加 `!HasPendingHookConfirm && !HookBusyConfirmedByTranscript` 两个抑制条件。
- `UpdateStatus`：`ResetHookBusyConfirmed` 仅在真正 `Idle->Busy` 时调用；`Busy->Busy` 刷新（同 turn 下一个 PreToolUse）保留确认标志，避免 mid-turn 误报。

**`HookServer.cs`** — 临时调试日志块已移除（恢复原状，无 diff）。

### 编译验证
- Debug：0 警告 0 错误。
- Release：`dotnet publish -r win-x64 -c Release` 成功，输出在 `ClaudeMonitor/bin/Release/net8.0-windows/win-x64/publish/`。

### 排查后清理
- ✅ HookServer.cs 的 TEMP DEBUG 块已移除。
- ✅ `hook-payload-debug.log` 已删除。
- ⏳ 诊断版进程（`AppData\Local\cc-pulse-diag-build`，PID 21672）仍在运行 — 用户将自行部署正式版替换。

## Step 4：修复后验证（部署完成 2026-08-09 18:10，验证进行中）

### 修复前基线（08-08 当天日志，部署前）
- `hook_missed` anomaly：**91 次**
- `grace_expired` anomaly：**75 次**
- `hookTracked=False`：**121 次** / `True`：**4 次**（transcript 几乎总比 hook 先到）
- `reconcile` 触发：**6 次**

### 部署执行（2026-08-09）
- 修复版 publish 输出（`ClaudeMonitor.exe` 251KB，08-08 23:22 编译）已拷贝到 `C:\Program Files\CC-Pulse\ClaudeMonitor.exe`（覆盖 08-05 旧版 249KB）。
- `CC-Pulse-Hook.exe` 未改动（本次修复不涉及 HookProxy/HookRunner），保留 08-05 版本。
- 正式版已启动（PID 34680），HookServer 监听 8765，08-09 日志开始写入。
- 诊断版进程（PID 21672）此前已自行退出，无需清理。

### 初步验证（08-09 18:10–18:11，2 个 tool_use 样本）
- transcript `tool_use` 仍为 `hookTracked=False`（符合根因：hook 滞后 transcript）。
- **`hook_missed` = 0，`grace_expired` = 0**——修复前此情形必触发 `hook_missed`，现被 `MarkPendingHookConfirm` 宽限吸收。✅
- 样本量不足，需在真实负载下积累更多 tool_use 后复测。

### 部署后预期（方向 b）
- `hook_missed` 降至接近 0（仅 hook 真丢失时才报，500ms 宽限覆盖 ~200ms 滞后）。
- `grace_expired` 降至接近 0（被 `HasPendingHookConfirm` / `HookBusyConfirmedByTranscript` 抑制）。
- `hookTracked=True` 比例不再有意义（不再即时判定，改延迟判定）。
- reconcile 正常触发（hook 与 transcript 协作而非互相对抗）。
- 对照 TASKS.md §10 完成标准中"Hook 丢失场景下状态 ≤3s 修正"。

### 验证步骤
1. ✅ 部署正式版（08-09 18:10），当天日志为干净基线（08-09 之前无日志）。
2. ⏳ 正常使用 Claude Code 一段时间（触发若干工具调用）——进行中。
3. ⏳ 对照上述指标确认异常降至接近 0。
4. ⏳ 若仍有偶发 `grace_expired`，检查是否为真异常（hook 确实丢失）而非时序误报。

## Step 5：启动时幽灵 session 修复（2026-08-10 完成）

### 现象
启动 CC-Pulse 后，窗口凭空出现一条 `CC-Pulse(main 工作中)` 记录，但用户根本没启动 session。

### 根因
`dc13344` 引入的 NDJSON 队列重放机制（TASKS.md §5）设计缺陷：`App.xaml.cs` 启动时调用 `QueueManager.Replay()`，把 `~/.claude/cc-pulse-queue.ndjson` 里**离线期间残留的历史 hook 事件**全部重放给 HookServer，复活了早已结束的 session 并卡在 Busy。

完整证据链：
- 队列文件残留 27 行历史事件（`83bce082...` 7 条 + `c8f7444f...` 20 条），`c8f7444f...` 最后一条是 `PreToolUse`（无配对 `PostToolUse`）。
- 重放按文件顺序逐行 POST：`start` → `AddSession` 创建 session；后续 `busy` → `TrackTool` + `UpdateStatus(Busy)`；末尾 `PreToolUse` 的 `TrackTool` 残留在 `_activeTools`，无 `PostToolUse` 清除 → `activeOps=True`。
- 日志 `cc-pulse-2026-08-10.log` 08:27:15 连续 7 条 `Idle->Busy activeOps=True`（重放到达），用户此时未启动任何 session。
- `activeOps=True` 使 watchdog 走 30 分钟长超时分支，幽灵 session 显示"工作中"长达半小时。

### 修复（方向 A：启动时清空队列而非重放）
- `QueueManager.Replay()` → `QueueManager.Discard()`：启动时直接删除队列文件，不重放。移除不再使用的 HTTP 重放逻辑、`_httpClient`、`HookServerUrl`、`TryExtractEndpoint`。保留 `Enqueue`（离线入队仍有诊断价值）。
- `App.xaml.cs`：调用点改为 `QueueManager.Discard()`，注释说明为何不重放（历史事件复活幽灵 session）。
- CC-Pulse 启动时从**当前真实状态**出发：`Discard` 丢弃历史队列，由 live `SessionStart` hook 建立真实 session。

### 验证（2026-08-10 08:56）
- 新版部署到 `C:\Program Files\CC-Pulse\ClaudeMonitor.exe`（250488 字节）。
- 残留队列文件已手动清除（`Discard` 启动时也会清）。
- 启动后日志只有 `=== CC-Pulse starting ===` 一行，**无 `Idle->Busy` 重放刷屏**。
- `/sessions` 返回 `(no sessions)`，窗口干净。
- reconciler 跑过多个 tick（2s 间隔）后仍无幽灵 session。

## 不在本次范围

- Subagent 内部状态（Phase 2 §5）：当前日志无 subagent 相关异常，暂不动。
- 回放测试框架（Step 5）：等根因修复后再建。
- 性能优化：当前 CPU/IO 无异常信号，非问题。

## 风险

- Step 1 改 HookProxy 需重新编译发布 `CC-Pulse-Hook.exe` 到 `C:/Program Files/CC-Pulse/`（MSI 安装位置）。若不便重发，可临时用 `ClaudeMonitor.exe hook` 路径（HookRunner）加日志，或直接在 HookServer 入口加日志（主程序内，无需动 proxy）。
- 调试日志可能记录 session_id 等信息，排查后务必移除。
