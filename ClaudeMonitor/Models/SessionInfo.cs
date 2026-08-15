using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;

namespace ClaudeMonitor.Models;

/// <summary>
/// Represents the status of a Claude Code session.
/// </summary>
public enum SessionStatus
{
    /// <summary>Session is idle, waiting for user input, or between tasks (green).</summary>
    Idle,

    /// <summary>Session is actively working — thinking, generating, or using tools (red).</summary>
    Busy
}

/// <summary>
/// Holds state for a single Claude Code session.
/// Implements INotifyPropertyChanged for WPF data binding.
/// </summary>
public class SessionInfo : INotifyPropertyChanged
{
    private string _sessionId = string.Empty;
    private SessionStatus _status = SessionStatus.Idle;
    private DateTime _lastUpdated = DateTime.Now;
    private string _projectPath = string.Empty;
    private string _displayName = string.Empty;
    private bool _subagentActive;
    private bool _hasSubagentActivity;
    private string _subagentDescription = string.Empty;
    private bool _isWorking;

    // ──────────────────────────────────────────────────────────────────
    //  Main-agent fine-grained state (the Idle/Busy refinement). Derived
    //  by SessionManager.DeriveMainFineState from the transcript fields
    //  above plus the user/assistant timestamps below. The UI binds
    //  MainStatusText to show "thinking…"/"running: Bash"/etc.
    // ──────────────────────────────────────────────────────────────────

    private MainAgentState _mainState = MainAgentState.Idle;
    private string _mainActiveToolName = string.Empty;
    private DateTime? _lastUserMessageUtc;
    private DateTime? _lastAssistantMessageUtc;
    private bool _isWaitingUser;
    private readonly object _mainFineStateLock = new();

    /// <summary>
    /// The set of currently active subagents for this session. Populated
    /// authoritatively by <see cref="Services.SubagentWatcher"/>; the hook path
    /// may also set <see cref="SubagentActive"/> for instant feedback before the
    /// watcher reconciles. The UI shows a single aggregate subagent row derived
    /// from this collection (<see cref="SubagentWorking"/>), not one row per
    /// subagent.
    /// </summary>
    public ObservableCollection<SubagentInfo> Subagents { get; } = new();

    /// <summary>
    /// Lock object used with <see cref="BindingOperations.EnableCollectionSynchronization"/>
    /// so the Subagents collection can be safely modified by the SubagentWatcher
    /// (background thread) while WPF binds to it on the UI thread.
    /// </summary>
    private readonly object _subagentsLock = new();

    /// <summary>Lock for thread-safe mutation of <see cref="Subagents"/>.</summary>
    internal object SubagentsLock => _subagentsLock;

    /// <summary>
    /// The set of currently in-flight main-agent tool_use_ids (PreToolUse fired
    /// but PostToolUse not yet). Used by the watchdog to apply a longer timeout
    /// while a tool is executing (TASKS.md §3.2): a long-running Bash compile
    /// should not be killed by the 60s busy timer. Tracked by tool_use_id so
    /// duplicate PreToolUse events are idempotent. Guarded by
    /// <see cref="_activeToolsLock"/>.
    /// </summary>
    private readonly HashSet<string> _activeTools = new();
    private readonly object _activeToolsLock = new();

    /// <summary>
    /// tool_use_ids observed in the transcript that are waiting for the
    /// (slower) PreToolUse hook to confirm them. The hook path lags the
    /// transcript by ~80-200ms (hook exe cold-start + HTTP round-trip vs.
    /// direct transcript write), so a transcript tool_use typically arrives
    /// BEFORE its PreToolUse. Checking <see cref="_activeTools"/> at that
    /// instant would always read false and falsely flag <c>hook_missed</c>.
    /// Instead we record the id here with the observation time and give the
    /// hook a grace window (<see cref="PendingHookConfirmTimeoutMs"/>); if
    /// <see cref="TrackTool"/> clears it in time, the hook confirmed. If it
    /// still sits here past the timeout, the PreToolUse hook was genuinely
    /// lost and the reconciler logs <c>hook_missed</c>. Guarded by
    /// <see cref="_pendingHookConfirmsLock"/>.
    /// </summary>
    private readonly Dictionary<string, DateTime> _pendingHookConfirms = new();
    private readonly object _pendingHookConfirmsLock = new();

    /// <summary>
    /// How long (ms) after the transcript sees a tool_use to wait for the
    /// PreToolUse hook before declaring it missed. Covers the observed
    /// ~200ms hook lag with margin.
    /// </summary>
    private const int PendingHookConfirmTimeoutMs = 500;

    /// <summary>
    /// True when there are in-flight main-agent tool calls (PreToolUse without
    /// a matching PostToolUse). Drives the watchdog's long-timeout branch.
    /// </summary>
    public bool HasActiveOperations
    {
        get
        {
            lock (_activeToolsLock)
            {
                return _activeTools.Count > 0;
            }
        }
    }

    /// <summary>
    /// True when at least one transcript tool_use is still awaiting a
    /// PreToolUse hook confirmation within the grace window. Used to suppress
    /// <c>grace_expired</c> while the hook is simply lagging (not lost).
    /// </summary>
    public bool HasPendingHookConfirm
    {
        get
        {
            lock (_pendingHookConfirmsLock)
            {
                return _pendingHookConfirms.Count > 0;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  Phase 2: Transcript-derived state (TASKS.md §4)
    // ──────────────────────────────────────────────────────────────────
    //  The transcript JSONL is the authoritative source of truth; hooks are
    //  low-latency triggers. These fields hold what the transcript observed,
    //  kept separate from the hook-driven fields above so the reconciler can
    //  compare and resolve conflicts. The final UI-bound <see cref="Status"/>
    //  is derived by the reconciler from both sources.

    /// <summary>
    /// tool_use_ids observed in the transcript (assistant.message.content[]
    /// with type "tool_use") that have not yet been paired with a matching
    /// tool_result (user.message.content[] with the same tool_use_id). When
    /// non-empty, the main agent is authoritatively Busy. Guarded by
    /// <see cref="_transcriptToolsLock"/>.
    /// </summary>
    private readonly HashSet<string> _transcriptActiveTools = new();
    private readonly object _transcriptToolsLock = new();

    /// <summary>
    /// Timestamp (UTC) of the most recent <c>system</c> entry with
    /// <c>subtype=="stop_hook_summary"</c> seen in the transcript — the
    /// authoritative turn-end signal (current Claude Code versions no longer
    /// write a top-level <c>result</c> entry). Null until the first turn end
    /// is observed. Used by the reconciler to authoritatively set Idle.
    /// </summary>
    private DateTime? _transcriptLastStopUtc;
    private readonly object _transcriptStopLock = new();

    /// <summary>
    /// The main-agent status as set by the hook path. The reconciler reads
    /// this (plus the transcript fields) to derive the final UI-bound
    /// <see cref="Status"/>. Kept distinct from <see cref="Status"/> so the
    /// two sources can be compared and conflicts logged as anomalies.
    /// </summary>
    private SessionStatus _hookState = SessionStatus.Idle;

    /// <summary>
    /// When the hook path last set <see cref="HookState"/> to Busy. Used by
    /// the reconciler's grace-period logic: a hook-driven Busy is trusted for
    /// up to <c>GracePeriodSeconds</c> while waiting for the transcript to
    /// confirm. Null when HookState is Idle or after confirmation.
    /// </summary>
    private DateTime? _hookBusyAtUtc;

    /// <summary>
    /// True once the transcript has confirmed the current hook-driven Busy
    /// period by observing a tool_use (or, for UserPromptSubmit, any
    /// transcript activity). The hook path lags the transcript, so a Busy is
    /// normally confirmed within the grace window. This flag lets the
    /// reconciler distinguish "hook Busy, transcript never confirmed" (a
    /// genuine grace_expired) from "hook Busy, transcript confirmed, then the
    /// tool finished and we are in a between-tools gap or waiting for Stop"
    /// (NOT an anomaly). Reset to false whenever the hook path (re)sets Busy.
    /// </summary>
    private bool _hookBusyConfirmedByTranscript;

    /// <summary>
    /// True once the reconciler has logged a <c>grace_expired</c> anomaly for
    /// the current hook-driven Busy period. Reset to false whenever the hook
    /// path (re)sets Busy, so the anomaly is logged ONCE per grace expiry
    /// rather than on every 2s reconcile tick while the state persists.
    /// </summary>
    private bool _graceExpiredLogged;

    /// <summary>
    /// Which source determined the current <see cref="Status"/>. Reported as
    /// the <c>source</c> field in the output structure (TASKS.md §6).
    /// </summary>
    private StateSource _stateSource = StateSource.Hook;

    /// <summary>
    /// Recent anomalies detected by the reconciler (TASKS.md §3.6/§4.3),
    /// newest last. Bounded to avoid unbounded growth; older entries are
    /// dropped. Guarded by <see cref="_anomaliesLock"/>.
    /// </summary>
    private readonly List<AnomalyRecord> _anomalies = new();
    private readonly object _anomaliesLock = new();
    private const int MaxAnomaliesKept = 50;

    /// <summary>
    /// tool_use_ids observed in the transcript that have not yet been paired
    /// with a tool_result. When non-empty, the main agent is authoritatively
    /// Busy (transcript is the source of truth).
    /// </summary>
    public bool TranscriptHasUnpairedToolUse
    {
        get
        {
            lock (_transcriptToolsLock)
            {
                return _transcriptActiveTools.Count > 0;
            }
        }
    }

    /// <summary>
    /// Timestamp (UTC) of the most recent transcript turn-end signal, or null
    /// if none has been observed yet.
    /// </summary>
    public DateTime? TranscriptLastStopUtc
    {
        get
        {
            lock (_transcriptStopLock)
            {
                return _transcriptLastStopUtc;
            }
        }
    }

    /// <summary>The main-agent status as last set by the hook path.</summary>
    public SessionStatus HookState
    {
        get => _hookState;
        set => SetField(ref _hookState, value);
    }

    /// <summary>When the hook path last marked the session Busy (UTC), for grace-period logic.</summary>
    public DateTime? HookBusyAtUtc
    {
        get => _hookBusyAtUtc;
        set => SetField(ref _hookBusyAtUtc, value);
    }

    /// <summary>
    /// Whether a <c>grace_expired</c> anomaly has already been logged for the
    /// current hook-driven Busy period. See <see cref="MarkGraceExpiredLogged"/>
    /// and <see cref="ResetGraceExpiredFlag"/>.
    /// </summary>
    public bool GraceExpiredLogged => _graceExpiredLogged;

    /// <summary>Mark that grace_expired was logged for this Busy period (suppress repeats).</summary>
    public void MarkGraceExpiredLogged() => _graceExpiredLogged = true;

    /// <summary>Reset the grace_expired flag (call when hook (re)sets Busy).</summary>
    public void ResetGraceExpiredFlag() => _graceExpiredLogged = false;

    /// <summary>
    /// Whether the transcript has confirmed the current hook-driven Busy
    /// period. See <see cref="MarkHookBusyConfirmedByTranscript"/> and
    /// <see cref="ResetHookBusyConfirmed"/>.
    /// </summary>
    public bool HookBusyConfirmedByTranscript => _hookBusyConfirmedByTranscript;

    /// <summary>
    /// Mark that the transcript confirmed the current hook Busy (a tool_use
    /// arrived). Suppresses grace_expired for the rest of this Busy period —
    /// a later between-tools gap or a wait-for-Stop is not an anomaly.
    /// </summary>
    public void MarkHookBusyConfirmedByTranscript() => _hookBusyConfirmedByTranscript = true;

    /// <summary>Reset the confirmation flag (call when hook (re)sets Busy).</summary>
    public void ResetHookBusyConfirmed() => _hookBusyConfirmedByTranscript = false;

    /// <summary>Which source determined the current Status.</summary>
    public StateSource StateSource
    {
        get => _stateSource;
        set => SetField(ref _stateSource, value);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Main-agent fine-grained state (Idle/Busy refinement)
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The main agent's fine-grained internal state — the refinement of
    /// <see cref="Status"/> (Idle/Busy) into Idle/Thinking/ToolRunning/
    /// WaitingApi/WaitingUser. Derived by
    /// <see cref="Services.SessionManager.DeriveMainFineState"/> and applied
    /// by the reconciler. The UI binds <see cref="MainStatusText"/> (which
    /// switches on this) to show a specific label instead of a generic
    /// "Working…". Reduces to <see cref="Status"/> for the watchdog/tray.
    /// </summary>
    public MainAgentState MainState
    {
        get => _mainState;
        set
        {
            if (SetField(ref _mainState, value))
            {
                // MainStatusText derives from MainState (+ MainActiveToolName),
                // and IsWorking reduces from the coarse Status (unchanged here),
                // so only the text binding needs re-notification.
                OnPropertyChanged(nameof(MainStatusText));
            }
        }
    }

    /// <summary>
    /// Name of the tool currently executing on the main agent (when
    /// <see cref="MainState"/> is <see cref="MainAgentState.ToolRunning"/>),
    /// empty otherwise. Reported in the fine-grained status text
    /// ("running: Bash").
    /// </summary>
    public string MainActiveToolName
    {
        get => _mainActiveToolName;
        set
        {
            if (SetField(ref _mainActiveToolName, value))
            {
                // Only relevant to the text while ToolRunning.
                if (_mainState == MainAgentState.ToolRunning)
                    OnPropertyChanged(nameof(MainStatusText));
            }
        }
    }

    /// <summary>
    /// UTC timestamp of the most recent REAL user message (a user entry that
    /// is NOT a tool_result) observed in the transcript. Used by
    /// <see cref="Services.SessionManager.DeriveMainFineState"/> to detect
    /// <see cref="MainAgentState.WaitingApi"/> (user prompt with no assistant
    /// response for &gt; 10s). Null until the first real user message, and
    /// cleared when a tool_result-bearing user entry arrives (so a tool
    /// completing does not trigger WaitingApi). Guarded by
    /// <see cref="_mainFineStateLock"/>.
    /// </summary>
    public DateTime? LastUserMessageUtc
    {
        get { lock (_mainFineStateLock) { return _lastUserMessageUtc; } }
    }

    /// <summary>
    /// UTC timestamp of the most recent assistant entry observed in the
    /// transcript. Used by <see cref="Services.SessionManager.DeriveMainFineState"/>
    /// to distinguish <see cref="MainAgentState.Thinking"/> (has assistant
    /// activity) from <see cref="MainAgentState.Idle"/> (no activity).
    /// Guarded by <see cref="_mainFineStateLock"/>.
    /// </summary>
    public DateTime? LastAssistantMessageUtc
    {
        get { lock (_mainFineStateLock) { return _lastAssistantMessageUtc; } }
    }

    /// <summary>
    /// True when the main agent is blocked waiting for user action (a
    /// permission approval or input request surfaced via a hook Notification).
    /// Set by <see cref="Services.SessionManager.SetWaitingUser"/>; cleared by
    /// the next Busy activity (<see cref="ClearWaitingUser"/>). While true,
    /// <see cref="DeriveMainFineState"/> returns
    /// <see cref="MainAgentState.WaitingUser"/> (which reduces to Idle).
    /// </summary>
    public bool IsWaitingUser
    {
        get { lock (_mainFineStateLock) { return _isWaitingUser; } }
    }

    /// <summary>
    /// Localized fine-grained status text for the main-agent row, reflecting
    /// <see cref="MainState"/> (and <see cref="MainActiveToolName"/> when
    /// ToolRunning). Binds the main row's TextBlock so the user sees
    /// "thinking…", "running: Bash", "waiting for API…", or "waiting for
    /// input…" instead of a generic "Working…"/"Idle".
    /// </summary>
    public string MainStatusText => ComputeMainStatusText();

    private string ComputeMainStatusText()
    {
        return MainState switch
        {
            MainAgentState.Thinking => ClaudeMonitor.Services.Lang.Get("MainStateThinking"),
            MainAgentState.ToolRunning => string.IsNullOrEmpty(MainActiveToolName)
                ? ClaudeMonitor.Services.Lang.Get("MainStateToolRunningGeneric")
                : ClaudeMonitor.Services.Lang.Get("MainStateToolRunning", MainActiveToolName),
            MainAgentState.WaitingApi => ClaudeMonitor.Services.Lang.Get("MainStateWaitingApi"),
            MainAgentState.WaitingUser => ClaudeMonitor.Services.Lang.Get("MainStateWaitingUser"),
            _ => ClaudeMonitor.Services.Lang.Get("MainStateIdle"),
        };
    }

    /// <summary>
    /// Record a real user message (a user entry that is NOT a tool_result)
    /// observed in the transcript at <paramref name="atUtc"/>. Updates
    /// <see cref="LastUserMessageUtc"/> for WaitingApi derivation. Guarded by
    /// <see cref="_mainFineStateLock"/>.
    /// </summary>
    public void RecordTranscriptUserMessage(DateTime atUtc)
    {
        lock (_mainFineStateLock)
        {
            _lastUserMessageUtc = atUtc;
        }
    }

    /// <summary>
    /// Record an assistant entry observed in the transcript at
    /// <paramref name="atUtc"/>. Updates <see cref="LastAssistantMessageUtc"/>
    /// for Thinking/Idle derivation. Guarded by
    /// <see cref="_mainFineStateLock"/>.
    /// </summary>
    public void RecordTranscriptAssistantMessage(DateTime atUtc)
    {
        lock (_mainFineStateLock)
        {
            _lastAssistantMessageUtc = atUtc;
        }
    }

    /// <summary>
    /// Mark that the main agent is waiting for user action (a permission
    /// approval or input request from a hook Notification). Sets the
    /// <see cref="IsWaitingUser"/> flag so the next reconcile derives
    /// <see cref="MainAgentState.WaitingUser"/>. Guarded by
    /// <see cref="_mainFineStateLock"/>.
    /// </summary>
    public void SetWaitingUser()
    {
        lock (_mainFineStateLock)
        {
            _isWaitingUser = true;
        }
    }

    /// <summary>
    /// Clear the waiting-for-user flag. Called when the next Busy activity
    /// arrives (the user approved/answered and the agent resumed). Guarded by
    /// <see cref="_mainFineStateLock"/>.
    /// </summary>
    public void ClearWaitingUser()
    {
        lock (_mainFineStateLock)
        {
            _isWaitingUser = false;
        }
    }

    /// <summary>
    /// Clear the main-agent fine-grained state tracking (on session reset /
    /// turn end). Resets the user/assistant timestamps and the waiting-for-user
    /// flag so a stale timestamp from a previous turn does not bleed into the
    /// next. Guarded by <see cref="_mainFineStateLock"/>.
    /// </summary>
    public void ClearMainFineStateTracking()
    {
        lock (_mainFineStateLock)
        {
            _lastUserMessageUtc = null;
            _lastAssistantMessageUtc = null;
            _isWaitingUser = false;
        }
    }

    /// <summary>
    /// Record a tool_use observed in the transcript (assistant entry). Pairs
    /// with <see cref="RecordTranscriptToolResult"/> by tool_use_id. Idempotent.
    /// </summary>
    public void RecordTranscriptToolUse(string toolUseId)
    {
        if (string.IsNullOrEmpty(toolUseId)) return;
        lock (_transcriptToolsLock)
        {
            _transcriptActiveTools.Add(toolUseId);
        }
    }

    /// <summary>
    /// Record a tool_result observed in the transcript (user entry), pairing
    /// the corresponding tool_use_id. Idempotent; ignores unknown ids.
    /// </summary>
    public void RecordTranscriptToolResult(string toolUseId)
    {
        if (string.IsNullOrEmpty(toolUseId)) return;
        lock (_transcriptToolsLock)
        {
            _transcriptActiveTools.Remove(toolUseId);
        }
    }

    /// <summary>
    /// Record that the transcript observed a turn-end signal
    /// (system.stop_hook_summary). Clears any unpaired tool_use ids and, if
    /// any were present, logs an anomaly (turn ended with tools still in
    /// flight). Returns true if unpaired tools were cleared.
    /// </summary>
    public bool RecordTranscriptTurnEnd(DateTime atUtc)
    {
        bool hadUnpaired;
        lock (_transcriptToolsLock)
        {
            hadUnpaired = _transcriptActiveTools.Count > 0;
            _transcriptActiveTools.Clear();
        }
        lock (_transcriptStopLock)
        {
            _transcriptLastStopUtc = atUtc;
        }
        // A turn end means the agent finished — clear the last real user
        // message timestamp so it cannot trigger WaitingApi after the turn
        // is over (the next WaitingApi can only come from a NEW user prompt
        // in the next turn).
        lock (_mainFineStateLock)
        {
            _lastUserMessageUtc = null;
        }
        if (hadUnpaired)
        {
            AddAnomaly(new AnomalyRecord(
                "stop_with_unpaired_tool_use", atUtc,
                "transcript turn-end (stop_hook_summary) seen with unpaired tool_use ids"));
        }
        return hadUnpaired;
    }

    /// <summary>
    /// Clear the transcript turn-end marker. Called when the hook path signals
    /// the start of a new turn (Busy) so that a stale stop from the PREVIOUS
    /// turn no longer authoritatively forces Idle while the new turn's
    /// tool_use has not yet landed on disk (§4.4 rule 2 vs rule 3). Without
    /// this, <see cref="TranscriptLastStopUtc"/> — once set, never null again —
    /// would permanently suppress the hook-driven grace-period Busy, and the
    /// main agent could never show Busy within the grace window after the
    /// first turn.
    /// </summary>
    public void ClearTranscriptTurnEnd()
    {
        lock (_transcriptStopLock)
        {
            _transcriptLastStopUtc = null;
        }
    }

    /// <summary>Clear all transcript-tracked tool ids (e.g. on session reset).</summary>
    public void ClearTranscriptTools()
    {
        lock (_transcriptToolsLock)
        {
            _transcriptActiveTools.Clear();
        }
        // Session reset also discards pending hook confirmations.
        ClearPendingHookConfirms();
        // And the main-agent fine-grained state tracking (user/assistant
        // timestamps, waiting-for-user flag) so a stale timestamp from the
        // previous turn does not bleed into the next.
        ClearMainFineStateTracking();
    }

    /// <summary>Add an anomaly record, bounding the list to <see cref="MaxAnomaliesKept"/>.</summary>
    public void AddAnomaly(AnomalyRecord record)
    {
        lock (_anomaliesLock)
        {
            _anomalies.Add(record);
            if (_anomalies.Count > MaxAnomaliesKept)
                _anomalies.RemoveAt(0);
        }
        System.Diagnostics.Debug.WriteLine(
            $"[CC-Pulse] anomaly: {record.Type} session={SessionId} {record.Detail}");
        Services.FileLogger.Anomaly(
            $"{record.Type} session={SessionId} {record.Detail}");
    }

    /// <summary>Snapshot of recent anomalies (newest last).</summary>
    public IReadOnlyList<AnomalyRecord> GetAnomalies()
    {
        lock (_anomaliesLock)
        {
            return _anomalies.ToArray();
        }
    }

    /// <summary>Track an in-flight tool_use_id (PreToolUse). Idempotent.</summary>
    public void TrackTool(string toolUseId)
    {
        if (string.IsNullOrEmpty(toolUseId)) return;
        lock (_activeToolsLock)
        {
            _activeTools.Add(toolUseId);
        }
        // The PreToolUse hook arrived for this id — it is no longer "pending
        // confirmation"; clear any entry the transcript path recorded so the
        // reconciler does not later flag it as hook_missed.
        ClearPendingHookConfirm(toolUseId);
    }

    /// <summary>
    /// Record a tool_use the transcript just observed, starting the grace
    /// window for its PreToolUse hook to arrive. Called by the transcript
    /// path instead of immediately flagging hook_missed (the hook lags the
    /// transcript; see <see cref="_pendingHookConfirms"/>).
    /// </summary>
    public void MarkPendingHookConfirm(string toolUseId, DateTime observedAtUtc)
    {
        if (string.IsNullOrEmpty(toolUseId)) return;
        lock (_pendingHookConfirmsLock)
        {
            _pendingHookConfirms[toolUseId] = observedAtUtc;
        }
    }

    /// <summary>
    /// Clear a pending confirmation for an id (the PreToolUse hook arrived).
    /// </summary>
    public void ClearPendingHookConfirm(string toolUseId)
    {
        if (string.IsNullOrEmpty(toolUseId)) return;
        lock (_pendingHookConfirmsLock)
        {
            _pendingHookConfirms.Remove(toolUseId);
        }
    }

    /// <summary>
    /// Remove and return the ids whose grace window has elapsed without a
    /// PreToolUse hook arriving — these are genuine hook misses. Called by
    /// the reconciler tick.
    /// </summary>
    public List<string> DrainExpiredPendingHookConfirms(DateTime nowUtc)
    {
        var expired = new List<string>();
        lock (_pendingHookConfirmsLock)
        {
            if (_pendingHookConfirms.Count == 0) return expired;
            var cutoff = nowUtc.AddMilliseconds(-PendingHookConfirmTimeoutMs);
            foreach (var kvp in _pendingHookConfirms)
            {
                if (kvp.Value <= cutoff)
                    expired.Add(kvp.Key);
            }
            foreach (var id in expired)
                _pendingHookConfirms.Remove(id);
        }
        return expired;
    }

    /// <summary>Clear all pending hook confirmations (on turn end / reset).</summary>
    public void ClearPendingHookConfirms()
    {
        lock (_pendingHookConfirmsLock)
        {
            _pendingHookConfirms.Clear();
        }
    }

    /// <summary>
    /// Whether a specific tool_use_id is currently tracked by the hook path
    /// (PreToolUse fired, PostToolUse not yet). Used by the reconciler to
    /// detect <c>hook_missed</c> anomalies precisely: a transcript tool_use
    /// whose id is NOT in this set arrived without a PreToolUse hook.
    /// </summary>
    public bool IsToolHookTracked(string toolUseId)
    {
        if (string.IsNullOrEmpty(toolUseId)) return false;
        lock (_activeToolsLock)
        {
            return _activeTools.Contains(toolUseId);
        }
    }

    /// <summary>Untrack a tool_use_id (PostToolUse). Idempotent; ignores unknown ids.</summary>
    public void UntrackTool(string toolUseId)
    {
        if (string.IsNullOrEmpty(toolUseId)) return;
        lock (_activeToolsLock)
        {
            _activeTools.Remove(toolUseId);
        }
    }

    /// <summary>Clear all in-flight tool ids (on Stop/StopFailure).</summary>
    public void ClearActiveTools()
    {
        lock (_activeToolsLock)
        {
            _activeTools.Clear();
        }
        // A turn end also discards any hook confirmations still pending — the
        // turn is over, so a late/missing PreToolUse is no longer actionable.
        ClearPendingHookConfirms();
        // And the last real user message timestamp — the turn is over, so a
        // stale user prompt cannot trigger WaitingApi after Stop.
        lock (_mainFineStateLock)
        {
            _lastUserMessageUtc = null;
        }
    }

    public SessionInfo()
    {
        // Allow cross-thread mutation of Subagents: WPF will acquire this lock
        // when raising CollectionChanged on the UI thread, and the watcher
        // acquires it when mutating. PropertyChanged notifications below still
        // need to fire; EnableCollectionSynchronization handles the collection
        // access, and INotifyPropertyChanged marshals to the UI thread by WPF.
        BindingOperations.EnableCollectionSynchronization(Subagents, _subagentsLock);

        // When the watcher adds/removes subagents, re-notify the derived
        // SubagentActive/SubagentWorking bindings so the row visibility and
        // indicator color update. The first observed subagent latches
        // HasSubagentActivity so the single subagent status row appears.
        Subagents.CollectionChanged += (_, _) =>
        {
            if (Subagents.Count > 0)
                HasSubagentActivity = true;
            else
                HasSubagentActivity = false;
            OnPropertyChanged(nameof(SubagentActive));
            OnPropertyChanged(nameof(SubagentWorking));
            OnPropertyChanged(nameof(SubagentStatusText));
            RefreshIsWorking();
        };
    }

    /// <summary>Unique session identifier from Claude Code.</summary>
    public string SessionId
    {
        get => _sessionId;
        set => SetField(ref _sessionId, value);
    }

    /// <summary>Current status of the session.</summary>
    public SessionStatus Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
                RefreshIsWorking();
        }
    }

    /// <summary>Timestamp of the last status update.</summary>
    public DateTime LastUpdated
    {
        get => _lastUpdated;
        set => SetField(ref _lastUpdated, value);
    }

    /// <summary>Project directory path associated with the session.</summary>
    public string ProjectPath
    {
        get => _projectPath;
        set => SetField(ref _projectPath, value);
    }

    /// <summary>Human-readable display name (derived from project path or session ID).</summary>
    public string DisplayName
    {
        get => _displayName;
        set => SetField(ref _displayName, value);
    }

    /// <summary>
    /// True when a subagent (spawned via the Agent/Task tool) is currently
    /// running in this session. Reflects both the hook-set flag (instant
    /// feedback) and the watcher-populated <see cref="Subagents"/> collection
    /// (authoritative). While active, the main agent is waiting, so the main
    /// status shows Idle and a separate subagent row shows Working.
    ///
    /// <para>The getter returns true when the hook flag is set OR when any
    /// subagent in the collection is in a working state
    /// (<see cref="SubagentInfo.IsWorking"/>). A subagent that has reached a
    /// terminal state (Completed/Failed) does NOT keep this true, so the
    /// main-agent Reconcile can resume and the UI subagent row flips to idle.
    /// The hook flag is cleared by <see cref="Services.SessionManager.UpdateSubagentState"/>
    /// once all subagents are terminal.</para>
    /// </summary>
    public bool SubagentActive
    {
        get
        {
            if (_subagentActive) return true;
            lock (_subagentsLock)
            {
                for (int i = 0; i < Subagents.Count; i++)
                {
                    if (Subagents[i].IsWorking) return true;
                }
            }
            return false;
        }
        set
        {
            if (SetField(ref _subagentActive, value))
            {
                if (value)
                    HasSubagentActivity = true;
                // SubagentWorking derives from SubagentActive, notify its binding
                OnPropertyChanged(nameof(SubagentWorking));
                RefreshIsWorking();
            }
        }
    }

    /// <summary>
    /// True while the session has at least one subagent row in its
    /// <see cref="Subagents"/> collection. The single subagent status row is
    /// only visible while this is true, so sessions that never spawn a
    /// subagent show no subagent row at all. Set true when the first subagent
    /// is added; set false when the collection empties (all subagents have
    /// reached a terminal state and been removed), so the row disappears
    /// cleanly rather than lingering as a stale "idle" row.
    /// </summary>
    public bool HasSubagentActivity
    {
        get => _hasSubagentActivity;
        private set => SetField(ref _hasSubagentActivity, value);
    }

    /// <summary>
    /// Description of the active subagent task (from the Agent tool's
    /// description field), shown in the subagent status row. Empty when no
    /// subagent is active.
    /// </summary>
    public string SubagentDescription
    {
        get => _subagentDescription;
        set => SetField(ref _subagentDescription, value);
    }

    /// <summary>
    /// True when the main agent is Busy. Binds the main-agent indicator circle
    /// so it reflects only the main agent's state (red while the main agent
    /// works, green while idle — including when a subagent is running and the
    /// main agent is waiting). The subagent has its own indicator bound to
    /// <see cref="SubagentWorking"/>.
    /// </summary>
    public bool IsWorking
    {
        get => _isWorking;
        private set => SetField(ref _isWorking, value);
    }

    /// <summary>
    /// True when a subagent is currently working (in a non-terminal state:
    /// Pending/Thinking/ToolRunning/WaitingApi). Binds the subagent indicator
    /// circle (red while the subagent works; green/idle once all subagents
    /// have reached a terminal state). The subagent row itself stays visible
    /// (gated by <see cref="HasSubagentActivity"/>) so the idle transition is
    /// shown briefly before the row is removed by SubagentStop/watcher.
    /// </summary>
    public bool SubagentWorking => SubagentActive;

    /// <summary>
    /// Localized status text for the subagent row, reflecting the fine-grained
    /// state of the most active subagent (TASKS.md §5.1/§6). Binds the
    /// subagent row's TextBlock so the user sees "thinking…", "running: Bash",
    /// "waiting for API…", "done", or "failed" instead of a generic
    /// "Working…"/"Idle". Returns the idle label when no subagent is working.
    /// </summary>
    public string SubagentStatusText => ComputeSubagentStatusText();

    /// <summary>
    /// Compute the subagent row text from the current collection. Picks the
    /// "most active" subagent (ToolRunning > Thinking > WaitingApi > Pending >
    /// terminal) so a tool-running subagent is reported even when a sibling
    /// has finished. Returns the idle label when the collection is empty or
    /// all subagents are terminal.
    /// </summary>
    private string ComputeSubagentStatusText()
    {
        SubagentInfo? pick = null;
        int bestRank = -1;
        lock (_subagentsLock)
        {
            for (int i = 0; i < Subagents.Count; i++)
            {
                var s = Subagents[i];
                int rank = s.State switch
                {
                    SubagentState.ToolRunning => 5,
                    SubagentState.Thinking => 4,
                    SubagentState.WaitingApi => 3,
                    SubagentState.Pending => 2,
                    SubagentState.Failed => 1,
                    SubagentState.Completed => 0,
                    _ => 0,
                };
                if (rank > bestRank)
                {
                    bestRank = rank;
                    pick = s;
                }
            }
        }

        if (pick is null)
            return ClaudeMonitor.Services.Lang.Get("StatusSubagentIdle");

        return pick.State switch
        {
            SubagentState.Pending => ClaudeMonitor.Services.Lang.Get("SubagentStatePending"),
            SubagentState.Thinking => ClaudeMonitor.Services.Lang.Get("SubagentStateThinking"),
            SubagentState.ToolRunning => string.IsNullOrEmpty(pick.ActiveToolName)
                ? ClaudeMonitor.Services.Lang.Get("SubagentStateToolRunningGeneric")
                : ClaudeMonitor.Services.Lang.Get("SubagentStateToolRunning", pick.ActiveToolName),
            SubagentState.WaitingApi => ClaudeMonitor.Services.Lang.Get("SubagentStateWaitingApi"),
            SubagentState.Completed => ClaudeMonitor.Services.Lang.Get("SubagentStateCompleted"),
            SubagentState.Failed => ClaudeMonitor.Services.Lang.Get("SubagentStateFailed"),
            _ => ClaudeMonitor.Services.Lang.Get("StatusSubagentIdle"),
        };
    }

    /// <summary>Recompute IsWorking from main Status only.</summary>
    private void RefreshIsWorking()
    {
        IsWorking = _status == SessionStatus.Busy;
    }

    /// <summary>
    /// Notify bindings that the derived subagent aggregate state
    /// (<see cref="SubagentActive"/>, <see cref="SubagentWorking"/>) may have
    /// changed because a subagent's <see cref="SubagentInfo.State"/> was
    /// updated. Called by <see cref="Services.SessionManager.UpdateSubagentState"/>
    /// after it mutates a row's State. Without this, the computed
    /// SubagentActive/SubagentWorking getters would return new values but WPF
    /// would not re-query them (no PropertyChanged was raised for them).
    /// </summary>
    internal void NotifySubagentAggregateChanged()
    {
        OnPropertyChanged(nameof(SubagentActive));
        OnPropertyChanged(nameof(SubagentWorking));
        OnPropertyChanged(nameof(SubagentStatusText));
        RefreshIsWorking();
    }

    /// <summary>
    /// Derives a display name from the project path or session ID.
    /// Shows the last folder name from the path, or a shortened session ID.
    /// </summary>
    public void UpdateDisplayName()
    {
        if (!string.IsNullOrEmpty(ProjectPath))
        {
            try
            {
                DisplayName = System.IO.Path.GetFileName(ProjectPath.TrimEnd('\\', '/'));
            }
            catch
            {
                DisplayName = SessionId.Length > 8 ? SessionId[..8] + "…" : SessionId;
            }
        }
        else
        {
            DisplayName = SessionId.Length > 8 ? SessionId[..8] + "…" : SessionId;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

/// <summary>Event arguments for session status changes.</summary>
public class SessionStatusChangedEventArgs : EventArgs
{
    public string SessionId { get; init; } = string.Empty;
    public SessionStatus OldStatus { get; init; }
    public SessionStatus NewStatus { get; init; }
    public SessionInfo Session { get; init; } = null!;

    /// <summary>
    /// True when this event represents a subagent-active flag change rather
    /// than (or in addition to) a main status change. UI handlers use this to
    /// know they must refresh the subagent row.
    /// </summary>
    public bool SubagentChanged { get; init; }
}
