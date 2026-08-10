using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ClaudeMonitor.Models;

namespace ClaudeMonitor.Services;

/// <summary>
/// Thread-safe manager for all active Claude Code sessions.
/// Maintains session state and raises events when status changes.
/// Includes a watchdog timer that resets sessions to Idle if no
/// activity is detected within the timeout period, handling the
/// case where Claude Code is interrupted (Ctrl+C / ESC) and the
/// Stop hook does not fire.
/// </summary>
public class SessionManager : IDisposable
{
    /// <summary>
    /// Timeout after which a Busy session is automatically reset to Idle.
    /// Claude Code's Stop hook does not fire on user interrupts (Ctrl+C / ESC),
    /// so this watchdog ensures the traffic light eventually turns green.
    /// The timer is reset on every activity (PreToolUse, PostToolUse, etc.).
    /// </summary>
    private const int BusyTimeoutSeconds = 60;

    /// <summary>
    /// Extended timeout applied while the session has in-flight tool calls
    /// (ActiveTools non-empty). A long-running tool (Bash compile, large file
    /// processing) emits no hooks between PreToolUse and PostToolUse, so the
    /// short 60s timer would prematurely reset the session to Idle. Per
    /// TASKS.md §3.2, tool execution should not be interrupted by the
    /// watchdog. 30 minutes covers virtually all real tool runs; if it still
    /// expires, the anomaly is logged before resetting.
    /// </summary>
    private const int LongBusyTimeoutSeconds = 1800;

    /// <summary>
    /// Timeout after which a subagent-active flag is automatically cleared.
    /// SubagentStop is not reliably fired by Claude Code, so this watchdog
    /// ensures the subagent row eventually disappears. The timer is reset on
    /// any main-session activity (the main agent resuming work implies the
    /// subagent has returned). Longer than the busy timeout because subagents
    /// often run longer than a single tool call.
    /// </summary>
    private const int SubagentTimeoutSeconds = 120;

    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly ConcurrentDictionary<string, System.Threading.Timer> _busyTimers = new();
    private readonly ConcurrentDictionary<string, System.Threading.Timer> _subagentTimers = new();

    /// <summary>
    /// Filesystem watcher that authoritatively detects subagent activity by
    /// scanning each session's subagents/ directory. May be null when the
    /// SessionManager is used standalone (e.g. CLI). Set by the app at startup.
    /// </summary>
    private SubagentWatcher? _subagentWatcher;

    /// <summary>Raised when any session's status changes.</summary>
    public event EventHandler<SessionStatusChangedEventArgs>? StatusChanged;

    /// <summary>Raised when a session is added or removed (affects aggregate status).</summary>
    public event EventHandler? SessionsChanged;

    /// <summary>Gets all active sessions as a snapshot.</summary>
    public IReadOnlyList<SessionInfo> GetAllSessions() => _sessions.Values.ToList();

    /// <summary>Gets the number of active sessions.</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>
    /// Attach the filesystem-based subagent watcher. The watcher is started by
    /// the caller; this only stores the reference so AddSession/RemoveSession
    /// can register/unregister sessions with it.
    /// </summary>
    public void SetSubagentWatcher(SubagentWatcher watcher)
    {
        _subagentWatcher = watcher;
    }

    /// <summary>
    /// Gets the aggregate (worst) status across all sessions.
    /// Red > Green. Returns Idle if no sessions.
    /// </summary>
    public SessionStatus AggregateStatus
    {
        get
        {
            if (_sessions.IsEmpty) return SessionStatus.Idle;

            var max = SessionStatus.Idle;
            foreach (var session in _sessions.Values)
            {
                if (session.Status > max)
                    max = session.Status;
            }
            return max;
        }
    }

    /// <summary>Register a new session.</summary>
    public void AddSession(string sessionId, string projectPath = "")
    {
        var session = new SessionInfo
        {
            SessionId = sessionId,
            ProjectPath = projectPath,
            Status = SessionStatus.Idle,
            LastUpdated = DateTime.Now
        };
        session.UpdateDisplayName();

        var oldStatus = _sessions.TryGetValue(sessionId, out var existing) ? existing.Status : SessionStatus.Idle;
        _sessions[sessionId] = session;

        StatusChanged?.Invoke(this, new SessionStatusChangedEventArgs
        {
            SessionId = sessionId,
            OldStatus = oldStatus,
            NewStatus = SessionStatus.Idle,
            Session = session
        });

        SessionsChanged?.Invoke(this, EventArgs.Empty);

        // Register with the subagent watcher so this session's subagents/
        // directory is polled for subagent activity.
        _subagentWatcher?.RegisterSession(sessionId, projectPath);

        // Activate transcript tailing so the main-agent state machine can be
        // driven authoritatively by the JSONL (Phase 2 §4). The tailer reads
        // only NEW lines (offset initialized to EOF), so launching mid-session
        // does not replay history.
        _transcriptTailer?.ActivateFile(sessionId, projectPath);
    }

    /// <summary>
    /// Update a session's status from the HOOK path. This is the low-latency
    /// trigger (§4): it sets <see cref="SessionInfo.HookState"/> and the
    /// grace-period timestamp, applies the status immediately for instant UI
    /// feedback, and triggers a reconcile so the transcript can correct it
    /// once it lands on disk. The transcript is the authority; if it later
    /// disagrees, the reconciler overrides <see cref="SessionInfo.Status"/>.
    /// </summary>
    public void UpdateStatus(string sessionId, SessionStatus newStatus)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        var oldStatus = session.Status;

        // A hook-driven Busy (re)starts the grace window EVERY time it arrives,
        // even if Status is already Busy. Within one turn, multiple PreToolUse
        // hooks fire (one per tool); each must refresh the grace-period start
        // so a long multi-tool turn is not mis-flagged as grace_expired after
        // the first 2s. It also clears a stale TranscriptLastStopUtc from the
        // PREVIOUS turn so rule 2 does not suppress this turn hook-driven
        // Busy while the new tool_use has not yet landed on disk (§4.4).
        //
        // The confirmation flag (HookBusyConfirmedByTranscript) is reset ONLY
        // on a genuine new Busy period (Idle->Busy). A Busy->Busy refresh
        // (the next PreToolUse in the same turn) must PRESERVE it: the
        // transcript often confirms the turn's first tool_use before the
        // first PreToolUse hook even arrives (hook lags transcript), and each
        // subsequent PreToolUse would otherwise wipe that confirmation and
        // re-enable grace_expired mid-turn.
        if (newStatus == SessionStatus.Busy)
        {
            session.ClearTranscriptTurnEnd();
            session.ResetGraceExpiredFlag();
            if (oldStatus != SessionStatus.Busy)
                session.ResetHookBusyConfirmed();
            session.HookBusyAtUtc = DateTime.UtcNow;
        }
        else
        {
            session.HookBusyAtUtc = null;
        }

        // Skip the rest if nothing actually changed (same status AND same hook
        // state). The grace refresh above still ran, which is intended.
        if (oldStatus == newStatus && session.HookState == newStatus)
        {
            // Status unchanged, but we refreshed grace. Reconcile so the
            // updated HookBusyAtUtc takes effect immediately.
            Reconcile(sessionId);
            return;
        }

        // Record the hook's view (§4.3).
        session.HookState = newStatus;

        // Apply immediately for low-latency UI feedback. The reconciler may
        // override this within the grace window if the transcript disagrees.
        session.Status = newStatus;
        session.LastUpdated = DateTime.Now;

        FileLogger.Info(
            $"hook {sessionId}: {oldStatus}->{newStatus} " +
            $"activeOps={session.HasActiveOperations} subagent={session.SubagentActive}");

        // Manage the watchdog timer based on the new status
        if (newStatus == SessionStatus.Busy)
        {
            StartOrResetBusyTimer(sessionId);
        }
        else
        {
            StopBusyTimer(sessionId);
        }

        StatusChanged?.Invoke(this, new SessionStatusChangedEventArgs
        {
            SessionId = sessionId,
            OldStatus = oldStatus,
            NewStatus = newStatus,
            Session = session
        });

        SessionsChanged?.Invoke(this, EventArgs.Empty);

        // Trigger an immediate reconcile so the transcript can correct drift
        // as soon as it has caught up (rather than waiting for the poll).
        Reconcile(sessionId);
    }

    /// <summary>
    /// Reset the busy watchdog timer for a session without changing its status.
    /// Called on every activity hook (PreToolUse, PostToolUse, UserPromptSubmit)
    /// to keep the timer alive while Claude Code is actively working.
    /// Also resets the subagent watchdog when a subagent is active, since any
    /// main-session activity implies the subagent is still relevant (or has
    /// just returned and will be cleared by the caller).
    /// </summary>
    public void ResetBusyTimeout(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        if (session.Status == SessionStatus.Busy)
        {
            StartOrResetBusyTimer(sessionId);
        }

        if (session.SubagentActive)
        {
            StartOrResetSubagentTimer(sessionId);
        }
    }

    /// <summary>
    /// Set the subagent-active flag for a session. Called when the main agent
    /// invokes the Agent tool (subagent started) or when the subagent is
    /// detected as finished (SubagentStop, main agent resuming, Stop, or
    /// watchdog timeout).
    ///
    /// When a subagent becomes active, the main status is set to Idle (the
    /// main agent is waiting) and the subagent watchdog is started. When it is
    /// cleared, the watchdog is stopped; the main status is left as-is so the
    /// caller's subsequent UpdateStatus call (or the existing Idle) applies.
    /// </summary>
    public void SetSubagentActive(string sessionId, bool active, string description = "")
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        var wasActive = session.SubagentActive;
        if (wasActive == active && active)
        {
            // Already active — just refresh description and reset watchdog
            session.SubagentDescription = description;
            StartOrResetSubagentTimer(sessionId);
            return;
        }
        if (wasActive == active && !active) return; // already inactive, nothing to do

        session.SubagentActive = active;
        session.SubagentDescription = active ? description : string.Empty;
        session.LastUpdated = DateTime.Now;

        if (active)
        {
            // Main agent is now waiting for the subagent → show Idle. Sync the
            // hook-derived state too: a stale HookState=Busy / HookBusyAtUtc
            // from before the subagent started would otherwise resurface via
            // DeriveMainState rule 4 once the subagent ends and Reconcile runs
            // again, wrongly flipping main back to Busy. The subagent row is
            // the active signal now; main is authoritatively Idle.
            var oldStatus = session.Status;
            session.Status = SessionStatus.Idle;
            session.HookState = SessionStatus.Idle;
            session.HookBusyAtUtc = null;
            session.ResetGraceExpiredFlag();
            StopBusyTimer(sessionId);
            StartOrResetSubagentTimer(sessionId);

            StatusChanged?.Invoke(this, new SessionStatusChangedEventArgs
            {
                SessionId = sessionId,
                OldStatus = oldStatus,
                NewStatus = SessionStatus.Idle,
                Session = session,
                SubagentChanged = true
            });
        }
        else
        {
            StopSubagentTimer(sessionId);

            StatusChanged?.Invoke(this, new SessionStatusChangedEventArgs
            {
                SessionId = sessionId,
                OldStatus = session.Status,
                NewStatus = session.Status,
                Session = session,
                SubagentChanged = true
            });
        }

        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Replace a session's active subagent list with the given set. Called
    /// authoritatively by <see cref="SubagentWatcher"/> after each poll.
    /// Reconciles the <see cref="SessionInfo.Subagents"/> collection in place
    /// by matching <see cref="SubagentInfo.AgentId"/> (NOT by position) so a
    /// subagent's row is tied to its identity: when subagent A ends and
    /// subagent B starts, B is not squeezed into A's old row slot (which left
    /// A's display name visible until the next poll). Rows for ended subagents
    /// are removed; new subagents are appended; surviving rows are updated.
    /// Also manages the subagent watchdog: active → start/reset, empty → stop.
    /// </summary>
    public void UpdateSubagents(string sessionId, IReadOnlyList<SubagentInfo> activeSubagents)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        var count = activeSubagents.Count;

        // Reconcile by AgentId so each row tracks its own subagent. Hold the
        // session's subagents lock so WPF (which acquires the same lock via
        // EnableCollectionSynchronization) sees a consistent view.
        var current = session.Subagents;
        lock (session.SubagentsLock)
        {
            // Map active ids → source info for quick lookup.
            var activeById = new Dictionary<string, SubagentInfo>(count, StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
                activeById[activeSubagents[i].AgentId] = activeSubagents[i];

            // Remove rows whose subagent is no longer active (iterate backward).
            for (int i = current.Count - 1; i >= 0; i--)
            {
                if (!activeById.ContainsKey(current[i].AgentId))
                    current.RemoveAt(i);
            }

            // Update surviving rows and append new ones, preserving the active
            // list's order. Reuse an existing row with the same AgentId if
            // present; otherwise create one.
            var existingById = new Dictionary<string, SubagentInfo>(current.Count, StringComparer.Ordinal);
            foreach (var row in current)
                existingById[row.AgentId] = row;

            for (int i = 0; i < count; i++)
            {
                var src = activeSubagents[i];
                if (existingById.TryGetValue(src.AgentId, out var dst))
                {
                    if (dst.AgentType != src.AgentType) dst.AgentType = src.AgentType;
                    if (dst.Description != src.Description) dst.Description = src.Description;
                    if (dst.DisplayName != src.DisplayName) dst.DisplayName = src.DisplayName;
                }
                else
                {
                    var info = new SubagentInfo
                    {
                        AgentId = src.AgentId,
                        AgentType = src.AgentType,
                        Description = src.Description,
                        DisplayName = src.DisplayName,
                    };
                    current.Add(info);
                    existingById[src.AgentId] = info;
                }
            }
        }

        // Clear the hook-set flag when the watcher sees no active subagents,
        // so SubagentActive reflects reality (not a stale hook signal).
        if (count == 0 && session.SubagentActive)
        {
            session.SubagentActive = false;
        }

        // Manage the watchdog based on watcher state.
        if (count > 0)
        {
            StartOrResetSubagentTimer(sessionId);

            // Authoritative main-status correction: while a subagent is
            // working, the main agent is waiting (Idle). Hooks can momentarily
            // set main to Busy on internal activity while a subagent runs;
            // the watcher overrides that here so the main indicator stays green.
            // Sync HookState/HookBusyAtUtc too so a stale hook Busy does not
            // resurface via DeriveMainState once the subagent ends.
            if (session.Status == SessionStatus.Busy)
            {
                var oldStatus = session.Status;
                session.Status = SessionStatus.Idle;
                session.HookState = SessionStatus.Idle;
                session.HookBusyAtUtc = null;
                session.ResetGraceExpiredFlag();
                StopBusyTimer(sessionId);
                StatusChanged?.Invoke(this, new SessionStatusChangedEventArgs
                {
                    SessionId = sessionId,
                    OldStatus = oldStatus,
                    NewStatus = SessionStatus.Idle,
                    Session = session,
                    SubagentChanged = true
                });
            }
        }
        else
        {
            StopSubagentTimer(sessionId);
        }

        session.LastUpdated = DateTime.Now;

        StatusChanged?.Invoke(this, new SessionStatusChangedEventArgs
        {
            SessionId = sessionId,
            OldStatus = session.Status,
            NewStatus = session.Status,
            Session = session,
            SubagentChanged = true
        });

        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Remove a single subagent's row from the session's <see cref="SessionInfo.Subagents"/>
    /// collection, identified by <paramref name="agentId"/>. Called by the
    /// SubagentStop hook (via HookServer) so a finished subagent disappears from
    /// the UI immediately, instead of waiting for the SubagentWatcher's
    /// stale-window to age it out. The watcher remains as a fallback for when
    /// SubagentStop does not fire.
    ///
    /// If the removed row was the last one, the subagent-active flag and
    /// watchdog are cleared. Raises SubagentChanged so the UI refreshes.
    /// </summary>
    public void RemoveSubagent(string sessionId, string agentId)
    {
        if (string.IsNullOrEmpty(agentId)) return;
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        var current = session.Subagents;
        bool removed = false;
        lock (session.SubagentsLock)
        {
            for (int i = current.Count - 1; i >= 0; i--)
            {
                if (string.Equals(current[i].AgentId, agentId, StringComparison.Ordinal))
                {
                    current.RemoveAt(i);
                    removed = true;
                    break;
                }
            }
        }

        if (!removed)
        {
            // The row wasn't present (watcher may have already aged it out, or
            // the subagent started before the watcher added it). Still clear the
            // scalar flag if no subagents remain, so SubagentActive reflects
            // reality.
            if (session.Subagents.Count == 0 && session.SubagentActive)
                session.SubagentActive = false;
            return;
        }

        // CollectionChanged (fired by RemoveAt) already re-notifies
        // SubagentActive/SubagentWorking. Clear the scalar flag and stop the
        // watchdog when no subagents remain.
        if (session.Subagents.Count == 0)
        {
            if (session.SubagentActive)
                session.SubagentActive = false;
            StopSubagentTimer(sessionId);
        }

        session.LastUpdated = DateTime.Now;

        StatusChanged?.Invoke(this, new SessionStatusChangedEventArgs
        {
            SessionId = sessionId,
            OldStatus = session.Status,
            NewStatus = session.Status,
            Session = session,
            SubagentChanged = true
        });

        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Whether a subagent is currently active for a session. Used by the hook
    /// path to decide whether main-agent tool activity should mark the main
    /// session Busy: while a subagent is running, the main agent is waiting, so
    /// main stays Idle regardless of incidental tool hooks.
    /// </summary>
    public bool IsSubagentActive(string sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var session) && session.SubagentActive;
    }

    /// <summary>
    /// Track an in-flight main-agent tool call by its tool_use_id (PreToolUse).
    /// Idempotent. The presence of active tools switches the watchdog to its
    /// long-timeout tier so a long tool run is not interrupted (§3.2).
    /// </summary>
    public void TrackTool(string sessionId, string toolUseId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        session.TrackTool(toolUseId);
    }

    /// <summary>
    /// Untrack a tool call by tool_use_id (PostToolUse). Idempotent; ignores
    /// unknown ids. After untracking, the caller should refresh the busy timer
    /// so the short timeout tier reapplies if no tools remain in flight.
    /// </summary>
    public void UntrackTool(string sessionId, string toolUseId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        session.UntrackTool(toolUseId);
    }

    /// <summary>
    /// Whether the session has in-flight main-agent tool calls. Used by the
    /// Stop/StopFailure path to detect event-chain breakage (§3.6).
    /// </summary>
    public bool HasActiveOperations(string sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var session) && session.HasActiveOperations;
    }

    /// <summary>Clear all in-flight tool ids for a session (on Stop/StopFailure).</summary>
    public void ClearActiveTools(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        session.ClearActiveTools();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Phase 2: Transcript-driven state (TASKS.md §4)
    // ──────────────────────────────────────────────────────────────────
    //  The transcript is the authoritative source of truth. These methods
    //  are called by TranscriptTailer when it observes tool_use,
    //  tool_result, or a turn-end signal in the JSONL. They update the
    //  session's transcript-derived fields and trigger a reconcile, which
    //  may override the hook-driven Status. Hooks remain the low-latency
    //  path; the transcript corrects drift once it lands on disk.

    /// <summary>
    /// Grace period during which a hook-driven Busy is trusted while waiting
    /// for the transcript to confirm (§4.3). The transcript has a small
    /// write delay, so a PreToolUse hook arrives before the corresponding
    /// tool_use line. Within this window, hook Busy wins; after it, if the
    /// transcript has not confirmed, the state is marked unconfirmed.
    /// </summary>
    private const int GracePeriodSeconds = 2;

    /// <summary>
    /// Reconciler poll interval. The reconciler runs on transcript events
    /// (immediate) AND on this timer, so that grace-period expiry and
    /// unconfirmed states are resolved even without new transcript lines.
    /// </summary>
    private const int ReconcilePollMs = 2000;

    private System.Threading.Timer? _reconcileTimer;
    private TranscriptTailer? _transcriptTailer;

    /// <summary>
    /// Attach the transcript tailer. Set by the app at startup so that
    /// AddSession/RemoveSession can activate/deactivate tailing.
    /// </summary>
    public void SetTranscriptTailer(TranscriptTailer tailer)
    {
        _transcriptTailer = tailer;
    }

    /// <summary>Start the reconciler background timer. Call once at startup.</summary>
    public void StartReconciler()
    {
        if (_reconcileTimer != null) return;
        _reconcileTimer = new System.Threading.Timer(_ => ReconcileAll(), null,
            ReconcilePollMs, ReconcilePollMs);
    }

    private void ReconcileAll()
    {
        foreach (var sessionId in _sessions.Keys)
        {
            try { Reconcile(sessionId); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Reconcile error for {sessionId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The transcript observed a tool_use (assistant entry) — the main agent
    /// is authoritatively starting a tool. Records it on the session and
    /// reconciles. If no PreToolUse hook announced this tool, logs a
    /// <c>hook_missed</c> anomaly (§4.3).
    /// </summary>
    public void OnTranscriptToolUse(string sessionId, string toolUseId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        session.RecordTranscriptToolUse(toolUseId);
        var hookTracked = session.IsToolHookTracked(toolUseId);
        FileLogger.Info($"transcript tool_use {sessionId}: id={toolUseId} hookTracked={hookTracked}");

        // The transcript observing a tool_use confirms the current hook-driven
        // Busy period. This suppresses grace_expired for the rest of the turn:
        // once the hook Busy has been confirmed, a later between-tools gap or
        // a wait-for-Stop is normal, not a lost confirmation.
        session.MarkHookBusyConfirmedByTranscript();

        // The hook path lags the transcript by ~80-200ms (hook exe cold-start
        // + HTTP round-trip vs. direct transcript write), so a transcript
        // tool_use usually arrives BEFORE its PreToolUse. Do NOT flag
        // hook_missed here — that would fire on nearly every tool. Instead
        // record the id with a grace window; TrackTool clears it when the
        // PreToolUse arrives, and the reconciler tick flags it as hook_missed
        // only if the window elapses with no confirmation (a genuine loss).
        if (!hookTracked)
        {
            session.MarkPendingHookConfirm(toolUseId, DateTime.UtcNow);
        }

        Reconcile(sessionId);
    }

    /// <summary>
    /// The transcript observed a tool_result (user entry) pairing a tool_use.
    /// Records it on the session and reconciles.
    /// </summary>
    public void OnTranscriptToolResult(string sessionId, string toolUseId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        session.RecordTranscriptToolResult(toolUseId);
        FileLogger.Info($"transcript tool_result {sessionId}: id={toolUseId} unpaired={session.TranscriptHasUnpairedToolUse}");
        Reconcile(sessionId);
    }

    /// <summary>
    /// The transcript observed a turn-end signal (system.stop_hook_summary) —
    /// the main agent is authoritatively Idle. Clears any unpaired tool_use
    /// (logging an anomaly if any) and reconciles to Idle.
    ///
    /// Stale-stop guard: the transcript tailer can lag behind the hook path
    /// (the grace period exists precisely for this). If a stop_hook_summary
    /// from the PREVIOUS turn arrives after the hook path has already marked
    /// the CURRENT turn Busy (HookBusyAtUtc set), and the stop's own timestamp
    /// predates that Busy start, the stop is stale — applying it would flip
    /// the current turn to Idle (rule 2) until the current tool_use lands,
    /// causing a Busy→Idle→Busy flicker. Ignore it.
    /// </summary>
    public void OnTranscriptTurnEnd(string sessionId, DateTime atUtc)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        if (session.HookBusyAtUtc is not null && atUtc < session.HookBusyAtUtc.Value)
        {
            FileLogger.Info($"transcript turn_end {sessionId}: STALE stop at {atUtc:o} ignored (hook Busy since {session.HookBusyAtUtc:o})");
            return;
        }
        var hadUnpaired = session.TranscriptHasUnpairedToolUse;
        session.RecordTranscriptTurnEnd(atUtc);
        FileLogger.Info($"transcript turn_end {sessionId}: at {atUtc:o} hadUnpaired={hadUnpaired}");
        Reconcile(sessionId);
    }

    /// <summary>
    /// Run the fusion state machine for a session: derive the authoritative
    /// main-agent status from the transcript fields (authoritative) and the
    /// hook state (low-latency, with grace period), and apply it to
    /// <see cref="SessionInfo.Status"/> if it differs (§4.4).
    ///
    /// Priority:
    ///  1. Transcript unpaired tool_use → Busy (authoritative)
    ///  2. Transcript turn-end (stop_hook_summary) → Idle (authoritative)
    ///  3. Hook Busy within grace period → Busy (transcript write delay)
    ///  4. Hook Idle → Idle
    ///  5. Otherwise: keep last known status
    /// </summary>
    public void Reconcile(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        // While a subagent is active, the main agent is waiting — main stays
        // Idle regardless of transcript/hook main-agent signals. The
        // SubagentWatcher already enforces this; skip main reconcile to avoid
        // fighting it.
        if (session.SubagentActive) return;

        var now = DateTime.UtcNow;

        // Drain transcript tool_uses whose PreToolUse hook grace window has
        // elapsed without confirmation. These are genuine hook misses (the
        // hook lag of ~200ms is well within the window, so a surviving entry
        // means the PreToolUse was truly lost). Logged here so both the 2s
        // tick and event-driven reconciles surface them promptly.
        var missed = session.DrainExpiredPendingHookConfirms(now);
        foreach (var id in missed)
        {
            session.AddAnomaly(new AnomalyRecord(
                "hook_missed", now,
                $"transcript tool_use {id} not confirmed by a PreToolUse hook within grace window"));
        }

        var derived = DeriveMainState(session, now, out var source);

        if (session.Status != derived)
        {
            ApplyReconciledStatus(sessionId, session, derived, source);
        }
        else if (session.StateSource != source)
        {
            // Status unchanged but source changed — update for reporting.
            session.StateSource = source;
        }
    }

    /// <summary>
    /// Derive the authoritative main-agent status from transcript + hook
    /// state (§4.4). Returns the derived status and the source that
    /// determined it.
    /// </summary>
    private SessionStatus DeriveMainState(SessionInfo session, DateTime nowUtc, out StateSource source)
    {
        // 1. Transcript unpaired tool_use → Busy (authoritative).
        if (session.TranscriptHasUnpairedToolUse)
        {
            source = StateSource.Transcript;
            return SessionStatus.Busy;
        }

        // 2. Transcript turn-end seen and no unpaired tools → Idle
        //    (authoritative). We treat the presence of a recent stop as
        //    authoritative Idle until a new tool_use or user prompt arrives.
        if (session.TranscriptLastStopUtc is not null)
        {
            source = StateSource.Transcript;
            return SessionStatus.Idle;
        }

        // 3. Hook Busy within grace period → trust the hook (transcript
        //    write delay). The hook fired PreToolUse/UserPromptSubmit before
        //    the transcript line landed.
        if (session.HookState == SessionStatus.Busy &&
            session.HookBusyAtUtc is not null &&
            (nowUtc - session.HookBusyAtUtc.Value).TotalSeconds <= GracePeriodSeconds)
        {
            source = StateSource.Reconciled;
            return SessionStatus.Busy;
        }

        // 4. Hook Busy but grace period expired without transcript
        //    confirmation → keep Busy but mark unconfirmed (anomaly). The
        //    alternative (flipping to Idle) would flicker on slow transcript
        //    writes; instead we hold and let the watchdog handle a true loss.
        if (session.HookState == SessionStatus.Busy)
        {
            if (session.HookBusyAtUtc is not null &&
                (nowUtc - session.HookBusyAtUtc.Value).TotalSeconds > GracePeriodSeconds)
            {
                // Grace expired without transcript confirmation. Log ONCE per
                // Busy period (the flag is reset when the hook next sets Busy)
                // so the 2s reconcile tick does not flood the anomaly list.
                //
                // Suppress when the hook is actively tracking a tool
                // (HasActiveOperations), when a transcript tool_use is still
                // within its PreToolUse grace window (HasPendingHookConfirm),
                // or when the transcript has ALREADY confirmed this Busy
                // period (HookBusyConfirmedByTranscript). The first two cover
                // hook lag; the third covers the normal between-tools gap or
                // wait-for-Stop that follows a confirmed tool — not a lost
                // confirmation. A genuinely stuck tool is handled by the 30min
                // watchdog (watchdog_timeout_with_active_ops); a truly
                // unconfirmed hook (no active tool, no pending confirmation,
                // never confirmed, no transcript line) still logs here.
                if (!session.HasActiveOperations
                    && !session.HasPendingHookConfirm
                    && !session.HookBusyConfirmedByTranscript
                    && !session.GraceExpiredLogged)
                {
                    session.AddAnomaly(new AnomalyRecord(
                        "grace_expired", nowUtc,
                        "hook Busy not confirmed by transcript within grace period"));
                    session.MarkGraceExpiredLogged();
                }
            }
            source = StateSource.Reconciled;
            return SessionStatus.Busy;
        }

        // 5. Hook Idle → Idle.
        if (session.HookState == SessionStatus.Idle)
        {
            source = StateSource.Hook;
            return SessionStatus.Idle;
        }

        // 6. Fallback: keep last known status.
        source = session.StateSource;
        return session.Status;
    }

    /// <summary>
    /// Apply a reconciled status to a session. This is the single point that
    /// writes <see cref="SessionInfo.Status"/> from the reconciler. It
    /// manages the watchdog timer and raises the usual change events, so the
    /// UI and tray update exactly as they would for a hook-driven change.
    /// </summary>
    private void ApplyReconciledStatus(string sessionId, SessionInfo session,
        SessionStatus newStatus, StateSource source)
    {
        var oldStatus = session.Status;
        session.StateSource = source;
        session.Status = newStatus;
        session.LastUpdated = DateTime.Now;

        // Reconciler-driven transitions are the most diagnostic: they show
        // the transcript overriding the hook (or vice versa). Log every one
        // so the state-machine trace is reconstructable from the log file.
        FileLogger.Info(
            $"reconcile {sessionId}: {oldStatus}->{newStatus} source={source} " +
            $"hook={session.HookState} unpaired={session.TranscriptHasUnpairedToolUse} " +
            $"stopUtc={session.TranscriptLastStopUtc?.ToString("o") ?? "null"}");

        if (newStatus == SessionStatus.Busy)
        {
            StartOrResetBusyTimer(sessionId);
        }
        else
        {
            StopBusyTimer(sessionId);
        }

        StatusChanged?.Invoke(this, new SessionStatusChangedEventArgs
        {
            SessionId = sessionId,
            OldStatus = oldStatus,
            NewStatus = newStatus,
            Session = session,
        });
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Record a compaction event for a session WITHOUT resetting its state
    /// (TASKS.md §3.5). Compaction is a continuation of the current session,
    /// not a new one: ActiveTools, Subagents, and the Busy/Idle status must be
    /// preserved. If the session is unknown (compact arrived before
    /// SessionStart, an unusual ordering), fall back to AddSession so it is at
    /// least tracked.
    /// </summary>
    public void MarkCompacting(string sessionId, string projectPath = "")
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            AddSession(sessionId, projectPath);
            return;
        }
        // Compaction starts a fresh transcript file: the old tool_use /
        // tool_result pairings and the previous turn-end marker no longer
        // correspond to anything on disk. Clear the transcript-derived turn
        // state so the reconciler does not act on stale signals. Hook-derived
        // state (ActiveTools, Subagents, Status) is preserved per §3.5 —
        // compaction is a continuation, not a new session.
        session.ClearTranscriptTools();
        session.ClearTranscriptTurnEnd();
        // Re-arm the tailer so its offset re-initializes to the new file's
        // EOF. Compact rewrites the transcript from scratch; if the new file
        // is larger than the old offset, the tailer would otherwise read
        // stale bytes from the middle of the new file. Deactivate+Activate
        // drops the old offset and starts fresh (only NEW appends read).
        if (_transcriptTailer is not null && !string.IsNullOrEmpty(projectPath))
        {
            _transcriptTailer.DeactivateFile(sessionId);
            _transcriptTailer.ActivateFile(sessionId, projectPath);
        }
        session.LastUpdated = DateTime.Now;
        System.Diagnostics.Debug.WriteLine(
            $"[CC-Pulse] compact: session {sessionId} state preserved ({session.Status})");
    }

    /// <summary>
    /// Log a state-machine anomaly for later analysis (TASKS.md §3.6). Examples:
    /// a Stop received while tools are still in flight (event-chain breakage),
    /// or a watchdog timeout while tools are in flight. Writes to Debug output.
    /// </summary>
    public void LogAnomaly(string sessionId, string type)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        var activeOps = session.HasActiveOperations;
        System.Diagnostics.Debug.WriteLine(
            $"[CC-Pulse] anomaly: {type} session={sessionId} status={session.Status} activeOps={activeOps}");
    }

    /// <summary>Remove a session (session ended).</summary>
    public void RemoveSession(string sessionId)
    {
        StopBusyTimer(sessionId);
        StopSubagentTimer(sessionId);
        _subagentWatcher?.UnregisterSession(sessionId);
        _transcriptTailer?.DeactivateFile(sessionId);

        if (_sessions.TryRemove(sessionId, out var session))
        {
            StatusChanged?.Invoke(this, new SessionStatusChangedEventArgs
            {
                SessionId = sessionId,
                OldStatus = session.Status,
                NewStatus = session.Status,
                Session = session
            });

            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Start or reset the watchdog timer for a Busy session. The timeout is
    /// tiered (TASKS.md §3.2): the short 60s timeout guards against a lost
    /// Stop hook during thinking/streaming, while the 30min long timeout
    /// applies when a tool is in flight (ActiveTools non-empty) so a long
    /// tool run is not interrupted. If the long timeout still expires, an
    /// anomaly is logged before the reset (§3.6).
    /// </summary>
    private void StartOrResetBusyTimer(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            StartOrResetBusyTimer(sessionId, BusyTimeoutSeconds, hasActiveOps: false);
            return;
        }
        var hasActiveOps = session.HasActiveOperations;
        var timeout = hasActiveOps ? LongBusyTimeoutSeconds : BusyTimeoutSeconds;
        StartOrResetBusyTimer(sessionId, timeout, hasActiveOps);
    }

    /// <summary>Timer factory with a chosen timeout and anomaly context.</summary>
    private void StartOrResetBusyTimer(string sessionId, int timeoutSeconds, bool hasActiveOps)
    {
        var timer = new System.Threading.Timer(_ =>
        {
            // The timer may have been superseded (a concurrent StartOrReset
            // replaced it in _busyTimers but this callback was already
            // queued) or the session may have already gone Idle by another
            // path (hook Stop, reconciler, subagent). Re-check before acting
            // so a stale callback does not flip a now-Idle (or removed)
            // session, and so two overlapping timers cannot both fire.
            if (!_sessions.TryGetValue(sessionId, out var s) ||
                s.Status != SessionStatus.Busy)
                return;

            // Timer expired — no activity detected within the window.
            if (hasActiveOps)
            {
                // A tool was in flight but never completed (PostToolUse lost,
                // or a genuinely stuck tool). Log the anomaly before resetting
                // so the event-chain breakage is visible for analysis (§3.6).
                LogAnomaly(sessionId, "watchdog_timeout_with_active_ops");
            }
            UpdateStatus(sessionId, SessionStatus.Idle);
        }, null, TimeSpan.FromSeconds(timeoutSeconds), Timeout.InfiniteTimeSpan);

        var oldTimer = _busyTimers.GetValueOrDefault(sessionId);
        _busyTimers[sessionId] = timer;

        // Dispose the old timer after replacing it
        oldTimer?.Dispose();
    }

    /// <summary>Stop and dispose the watchdog timer for a session.</summary>
    private void StopBusyTimer(string sessionId)
    {
        if (_busyTimers.TryRemove(sessionId, out var timer))
        {
            timer.Dispose();
        }
    }

    /// <summary>
    /// Start or reset the subagent watchdog timer. If it expires, the
    /// subagent-active flag is cleared (SubagentStop is not reliably fired).
    /// </summary>
    private void StartOrResetSubagentTimer(string sessionId)
    {
        var timer = new System.Threading.Timer(_ =>
        {
            // No activity for the timeout — clear the subagent flag
            SetSubagentActive(sessionId, false);
        }, null, TimeSpan.FromSeconds(SubagentTimeoutSeconds), Timeout.InfiniteTimeSpan);

        var oldTimer = _subagentTimers.GetValueOrDefault(sessionId);
        _subagentTimers[sessionId] = timer;

        oldTimer?.Dispose();
    }

    /// <summary>Stop and dispose the subagent watchdog timer for a session.</summary>
    private void StopSubagentTimer(string sessionId)
    {
        if (_subagentTimers.TryRemove(sessionId, out var timer))
        {
            timer.Dispose();
        }
    }

    public void Dispose()
    {
        // Dispose all watchdog timers
        foreach (var timer in _busyTimers.Values)
            timer.Dispose();
        _busyTimers.Clear();

        foreach (var timer in _subagentTimers.Values)
            timer.Dispose();
        _subagentTimers.Clear();

        _reconcileTimer?.Dispose();
        _reconcileTimer = null;

        _sessions.Clear();
    }
}
