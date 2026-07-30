# Plan: 修复 subagent 提前消失 + 双信号灯 UI

## 问题 1：subagent 状态提前消失

### 根因
`HookServer.cs` `case "busy"` 的兜底1（第 135-145 行）：subagent 激活期间，主 agent 任何非 Agent 工具调用都会立刻 `SetSubagentActive(false)`。但实测主 agent 在等待 subagent 时仍会触发 PreToolUse（内部活动 / subagent 完成通知到达），导致 subagent 行在终端打印 `finished` 前就消失。

### 修法
去掉兜底1。`case "busy"` 中 `toolName != "Agent"` 分支**不再清除 subagent 标记**，只做正常的 `UpdateStatus(Busy) + ResetBusyTimeout`。

subagent 结束只靠以下信号（均已实现）：
- SubagentStop hook → `/subagent-stop`（主信号，部分模式触发）
- Stop / StopFailure → `/idle`（turn 结束，兜底）
- 看门狗 120s 超时（兜底）

**注意**：去掉兜底1 后，若 SubagentStop 不触发，subagent 标记会一直挂到 turn 结束（Stop）或 120s 看门狗。这是可接受的——比「提前消失」好，因为 subagent 确实可能还在跑。

### 改动文件
- `ClaudeMonitor/Services/HookServer.cs`：`case "busy"` 的 else 分支移除 `SetSubagentActive(sessionId, false)` 调用，保留 `UpdateStatus(Busy)` + `ResetBusyTimeout`。更新注释说明为何不在主 agent 活动时清除 subagent 标记。

## 问题 2：双信号灯 UI

### 设计（已与用户确认）
- 每个 session 双灯纵向叠放：main 灯在上，subagent 灯在下
- main 灯：绿=Idle，红=Busy（独立反映 main 真实状态）
- subagent 灯：红=Working，整行隐藏=不活跃
- subagent 不活跃时，subagent 行（灯+文字）整行 Collapsed
- subagent 激活时：main 显示 Idle（绿），subagent 灯红——一眼区分「main 等 / subagent 跑」

### 改动 1：`SessionInfo.cs` — 新增 SubagentWorking 绑定属性
当前 `IsWorking` 是聚合（main busy OR subagent active），单灯用。双灯方案下：
- main 灯绑定 `IsWorking`（已是 main busy 的反映——但当前 `RefreshIsWorking` 把 subagent active 也算进去了，需改）
- subagent 灯绑定新属性 `SubagentWorking`（= SubagentActive）

**改 `RefreshIsWorking`**：`IsWorking = _status == SessionStatus.Busy`（不再 OR subagent）。这样 main 灯只反映 main 状态。subagent 激活时 main 是 Idle → IsWorking=false → 绿灯，符合设计。

**新增 `SubagentWorking` 属性**：只读绑定，返回 `_subagentActive`，在 `SubagentActive` setter 里触发 PropertyChanged。subagent 灯的 Fill 绑定它（用 StatusToColorConverter：true→红，false→绿，但 subagent 不活跃时整行隐藏所以 false 态不可见）。

### 改动 2：`StatusWindow.xaml` — 重构 session 卡片布局
当前布局：1 个 Ellipse（列0）+ 1 个 StackPanel（列1，含 DisplayName + main 行 + subagent 行）。

新布局：列0 改为**纵向 StackPanel 装两个 Ellipse**（main 灯 + subagent 灯），列1 保持 StackPanel 装文字行。但这样灯和文字行对齐会错位（灯列里两灯叠放，文字列里三行：DisplayName/main/subagent）。

更干净的方案：**整个 session 卡片改成纵向 StackPanel**，里面两个子 Grid 各一行：
- 行1（main 行）：[main 灯] [DisplayName + main 状态文字]
- 行2（subagent 行，Visibility 绑定 SubagentActive）：[subagent 灯] [subagent 状态文字]

```
<StackPanel>  <!-- session 卡片 -->
  <Grid>  <!-- main 行 -->
    <ColumnDef 20/><ColumnDef */>
    <Ellipse Grid.0 Fill={IsWorking→Color} />  <!-- main 灯 -->
    <StackPanel Grid.1>
      <TextBlock DisplayName />
      <TextBlock main 状态 />
    </StackPanel>
  </Grid>
  <Grid Visibility={SubagentActive→Visibility}>  <!-- subagent 行 -->
    <ColumnDef 20/><ColumnDef */>
    <Ellipse Grid.0 Fill={SubagentWorking→Color} />  <!-- subagent 灯 -->
    <TextBlock Grid.1 subagent 状态 />
  </Grid>
</StackPanel>
```

这样 main 灯和 subagent 灯各自对齐自己的文字行，subagent 行隐藏时整行消失，main 行不跳动。

### 改动 3：`StatusWindow.xaml.cs` — 转换器
- `StatusToColorConverter` 已有（bool→红/绿），main 灯和 subagent 灯都用它，无需改
- `MainStatusToTextConverter`、`SubagentStatusToTextConverter`、`BooleanToVisibilityConverter` 已有，无需改
- 无需新增转换器

### 改动 4：`Lang.cs` — 无需改
`StatusMainIdle` / `StatusMainBusy` / `StatusSubagentBusy` 已存在。

## 验证步骤
1. 编译 Release，安装到 `C:/Program Files/CC-Pulse/`
2. 在另一个 Claude Code session 触发 subagent（调用 Agent 工具）
3. 观察 CC-Pulse：
   - subagent 启动 → main 灯绿（Idle）+ subagent 灯红（Working），两行
   - subagent 跑期间 → subagent 行持续显示，不提前消失
   - subagent 结束（终端打印 finished 后）→ subagent 行消失，main 灯恢复
4. 回归：普通 main 工作 → main 灯红，无 subagent 行

## 风险
- 去掉兜底1 后，若 SubagentStop 不触发且 turn 没结束，subagent 行会挂到 120s 看门狗。可接受。
- `IsWorking` 语义改变（不再含 subagent）——需检查是否有其他地方绑定 `IsWorking` 期望含 subagent。已查：只有 XAML 的 Ellipse Fill 绑定它，且正是我们要改的。安全。
