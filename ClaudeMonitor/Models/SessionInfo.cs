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
            OnPropertyChanged(nameof(SubagentActive));
            OnPropertyChanged(nameof(SubagentWorking));
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
    /// </summary>
    public bool SubagentActive
    {
        get => _subagentActive || Subagents.Count > 0;
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
    /// Latch: true once a subagent has been detected in this session (via the
    /// Agent/Task hook or the filesystem watcher). The single subagent status
    /// row is only visible after this becomes true, so sessions that never
    /// spawn a subagent show no subagent row at all. Once set it stays true
    /// for the session's lifetime — the row persists after the last subagent
    /// finishes and flips to the idle state.
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
    /// True when a subagent is currently running. Binds the subagent indicator
    /// circle (red while the subagent works; the whole subagent row is hidden
    /// when false, so the green state is never visible).
    /// </summary>
    public bool SubagentWorking => SubagentActive;

    /// <summary>Recompute IsWorking from main Status only.</summary>
    private void RefreshIsWorking()
    {
        IsWorking = _status == SessionStatus.Busy;
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
