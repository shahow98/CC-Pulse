# 修复 subagent：结束后立即消失 + 串行场景正确显示

## 诊断结论（Tavily 搜索 + 磁盘实证）

**监听策略是对的。** Claude Code 确实把每个 subagent 写成
`~/.claude/projects/<encodedProj>/<sessionId>/subagents/agent-<id>.jsonl`，
正是 `SubagentWatcher` 在轮询的路径。磁盘实证：一个 session 目录里有 5 个并发
`agent-*.jsonl`（ffcs-librarian/architect/engineer×2），`agentType`/`description` 各异。
`workflows/` 子目录在所有项目里一个都没有（只 Workflow 工具场景才有）。第三方项目
`atc-claude-kanban` 监听的也是同一路径。所以"只显示一个"和"延迟消失"都不是监听策略错。

## 真正根因

### 根因A：结束后过一段时间才消失（用户要"立即消失"）

`SubagentStop` hook **从未被配置进用户的 `~/.claude/settings.json`**（实测只有
SessionStart/PreToolUse/PostToolUse/UserPromptSubmit/Stop/SessionEnd，没有 SubagentStop）。
当前运行的 ClaudeMonitor.exe 构建早于 `UsesMissingSubagentStop` 迁移逻辑，所以 hook 没补上。

即便 SubagentStop 被配置，当前链路也无法立即移除 subagent 行：
1. `HookRunner.Run` 只解析 `session_id`/`cwd`/`tool_name`，**不解析 `agent_id`**。
2. `HookServer` 的 `subagent-stop` 路由调 `SetSubagentActive(sessionId, false)`，
   只清 `SubagentActive` **标量**，**不清 `Subagents` 集合**里的具体行。
3. `SubagentActive` getter 是 `_subagentActive || Subagents.Count > 0`，标量清了但
   集合里行还在 → `Count > 0` → 行仍可见。
4. 唯一移除行的途径：watcher 的 45s 老化窗口。subagent 结束后 jsonl 不再追加，
   最后一行 timestamp 逐渐老化，45s 后才被 watcher 判为 stale → 行才消失。

**所以"过一段时间才消失"= 45s 老化延迟，不是 hook 驱动的即时清除。**

### 根因B：串行场景"只显示一个"

用户确认是**串行**（一个跑完再跑下一个）。串行时序：
- A 跑 → 集合 {A}，显示 1 行 ✓
- A 结束 → 无 SubagentStop hook → 集合仍 {A}（45s 内 watcher 仍判 active）→ 仍显示 A
- B 启动 → watcher 轮询 → active={A,B}（A 还在 45s 窗口）→ 集合应 {A,B} → 应显示 2 行

用户只看到 1 行，说明 A 在 B 启动前已老化出窗口（A 结束后超过 45s 才启动 B），
此时集合={B}，1 行——这是 45s 残留的副作用：A 残留期间显示的是"已结束的 A"，
用户误以为"只显示一个"。**根因B 是根因A 的衍生**：修好"立即消失"后，串行场景
自然变成"A 结束立即消失 → B 启动立即出现"，不再有残留混淆。

## 修复方案

核心思路：**让 SubagentStop hook 真正生效，并按 agent_id 精确移除集合行**，
subagent 结束即从 UI 消失，不再依赖 45s 老化。watcher 退化为兜底（hook 漏发时清理）。

### 改动1：HookRunner 解析 agent_id

`ClaudeMonitor/Services/HookRunner.cs` — `Run` 方法解析 stdin JSON 时增加
`agent_id`（SubagentStop/SubagentStart payload 字段，官方文档确认存在），放入 payload。
payload 字段名用 `agentId`。

### 改动2：HookServer 的 subagent-stop 路由按 agent_id 移除行

`ClaudeMonitor/Services/HookServer.cs`：
- `HandleRoute` 的 `subagent-stop` case：从 payload 取 `agentId`，调新方法
  `_sessionManager.RemoveSubagent(sessionId, agentId)` 精确移除该行。
- 若 payload 无 agentId（兜底），退化为 `SetSubagentActive(false)`（清标量），
  让 watcher 45s 老化兜底。
- `ParsePayload` 已是 `Dictionary<string,string>`，只需在 `HandleRouteAsync` 里多取
  `agentId` 并传入 `HandleRoute`。

### 改动3：SessionManager 新增 RemoveSubagent

`ClaudeMonitor/Services/SessionManager.cs`：
- 新增 `public void RemoveSubagent(string sessionId, string agentId)`：
  在 `SubagentsLock` 下按 `AgentId` 移除该行；若移除后集合空，清 `SubagentActive`
  标量 + `StopSubagentTimer`；raise `StatusChanged`(SubagentChanged=true) +
  `SessionsChanged`。`CollectionChanged` 会自动 re-notify `SubagentActive`/可见性。
- 保留 `SetSubagentActive`（hook 的 SubagentStart 仍用标量做即时点亮）。

### 改动4：确保 SubagentStop hook 被配置

`HooksConfig` 已含 SubagentStop（line 67），`UsesMissingSubagentStop` 迁移检测也在。
问题是用户当前构建旧、没跑过迁移。**修复后重新构建 + 运行一次 ClaudeMonitor.exe**
会触发 `EnsureHooksConfigured` → 检测到缺 SubagentStop → Remove + Configure 重配。
无需改 HookConfigurator 代码，只需重建。**但要在 plan 里明确这一步**。

### 改动5：watcher 窗口缩短为兜底

`ClaudeMonitor/Services/SubagentWatcher.cs`：
- `ActiveWindowSeconds` 45 → **20**。hook 现在即时移除行，watcher 只兜底 hook 漏发
  （SubagentStop 在某些模式不 fire，官方文档承认）。20s 足够覆盖 subagent 思考间隙
  （30s+ 无新行罕见），且 hook 漏发时残留最多 20s 而非 45s。
- 其余（timestamp 判定、2s 轮询、reconcile by AgentId）不变——已正确。

## 不改动

- XAML / SubagentInfo / SessionInfo 派生属性：绑定正确，`Subagents` 集合 + `SubagentActive`
  getter 已能驱动 UI；问题在数据源（hook 没清集合），不在 UI。
- `SubagentStart` hook：不配置。subagent 启动由 watcher 2s 内发现并加行，足够快；
  hook 的 `SetSubagentActive(true)` 标量点亮已被 watcher reconcile 覆盖，无需 SubagentStart。
- 120s watchdog：保留作最后兜底。

## 改动文件清单

1. `ClaudeMonitor/Services/HookRunner.cs` — 解析 `agent_id` 入 payload
2. `ClaudeMonitor/Services/HookServer.cs` — `subagent-stop` 路由取 agentId 调 `RemoveSubagent`
3. `ClaudeMonitor/Services/SessionManager.cs` — 新增 `RemoveSubagent(sessionId, agentId)`
4. `ClaudeMonitor/Services/SubagentWatcher.cs` — `ActiveWindowSeconds` 45 → 20

## 验证

- `dotnet build` 通过
- 重新运行 ClaudeMonitor.exe（触发 hook 重配，补上 SubagentStop）
- 人工：串行跑两个 subagent，确认 A 结束即从 UI 消失（不再残留 45s），B 启动即出现
- 兜底验证：若 SubagentStop 漏发，watcher 20s 内清理（可接受）
