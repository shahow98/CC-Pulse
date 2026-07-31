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
    }

    /// <summary>Update a session's status.</summary>
    public void UpdateStatus(string sessionId, SessionStatus newStatus)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        var oldStatus = session.Status;
        if (oldStatus == newStatus) return;

        session.Status = newStatus;
        session.LastUpdated = DateTime.Now;

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
            // Main agent is now waiting for the subagent → show Idle
            var oldStatus = session.Status;
            session.Status = SessionStatus.Idle;
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
            if (session.Status == SessionStatus.Busy)
            {
                var oldStatus = session.Status;
                session.Status = SessionStatus.Idle;
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

    /// <summary>Remove a session (session ended).</summary>
    public void RemoveSession(string sessionId)
    {
        StopBusyTimer(sessionId);
        StopSubagentTimer(sessionId);
        _subagentWatcher?.UnregisterSession(sessionId);

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
    /// Start or reset the watchdog timer for a Busy session.
    /// If the timer expires, the session is automatically set to Idle.
    /// </summary>
    private void StartOrResetBusyTimer(string sessionId)
    {
        var timer = new System.Threading.Timer(_ =>
        {
            // Timer expired — no activity detected, reset to Idle
            UpdateStatus(sessionId, SessionStatus.Idle);
        }, null, TimeSpan.FromSeconds(BusyTimeoutSeconds), Timeout.InfiniteTimeSpan);

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

        _sessions.Clear();
    }
}
