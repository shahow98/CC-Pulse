namespace ClaudeMonitor.Models;

/// <summary>
/// Fine-grained internal state of the main agent, derived by
/// <see cref="Services.SessionManager.DeriveMainFineState"/> from the main
/// transcript (<c>&lt;sessionId&gt;.jsonl</c>, the authoritative source) and
/// the hook path (low-latency triggers). This is the main-agent counterpart
/// of <see cref="SubagentState"/>: it refines the coarse
/// <see cref="SessionStatus"/> (Idle/Busy) into five states so the UI can show
/// "thinking…", "running: Bash", "waiting for API…", or "waiting for input…"
/// instead of a generic "Working…".
///
/// <para>The coarse <see cref="SessionStatus"/> shown by the tray/watchdog is
/// derived from this by reduction:
/// <see cref="Thinking"/>, <see cref="ToolRunning"/>, and
/// <see cref="WaitingApi"/> reduce to <see cref="SessionStatus.Busy"/>; the
/// main agent is actively working. <see cref="Idle"/> and
/// <see cref="WaitingUser"/> reduce to <see cref="SessionStatus.Idle"/>; the
/// main agent is not actively working (either truly idle or blocked on user
/// input).</para>
/// </summary>
public enum MainAgentState
{
    /// <summary>
    /// The main agent is idle — no turn in progress. The initial state, and
    /// the state after a turn-end signal (<c>stop_hook_summary</c>) with no
    /// subsequent user prompt. Reduces to <see cref="SessionStatus.Idle"/>.
    /// </summary>
    Idle,

    /// <summary>
    /// The main agent is reasoning or waiting for the model to start the next
    /// action within an active turn. The default Busy sub-state: there is
    /// assistant activity (or a hook-driven Busy) but no unpaired tool_use and
    /// no stale user message. Reduces to <see cref="SessionStatus.Busy"/>.
    /// </summary>
    Thinking,

    /// <summary>
    /// The main agent is actively executing a tool. The transcript has at
    /// least one tool_use without a matching tool_result. The active tool name
    /// is reported alongside (e.g. "running: Bash"). Reduces to
    /// <see cref="SessionStatus.Busy"/>.
    /// </summary>
    ToolRunning,

    /// <summary>
    /// The main agent sent a user message (a real user prompt, not a
    /// tool_result) and more than <c>WaitingApiThresholdSeconds</c> have
    /// elapsed with no assistant response. The agent is waiting for the API
    /// (rate limit, network). Reduces to <see cref="SessionStatus.Busy"/>.
    /// </summary>
    WaitingApi,

    /// <summary>
    /// The main agent is blocked waiting for the user to act — a permission
    /// approval or an explicit input request surfaced via a hook Notification.
    /// Set by the hook path (<see cref="Services.HookServer"/>); cleared when
    /// the next Busy activity arrives. Reduces to
    /// <see cref="SessionStatus.Idle"/> (the agent is not actively working).
    /// </summary>
    WaitingUser,
}
