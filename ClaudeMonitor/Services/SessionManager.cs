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

    /// <summary>Raised when any session's status changes.</summary>
    public event EventHandler<SessionStatusChangedEventArgs>? StatusChanged;

    /// <summary>Raised when a session is added or removed (affects aggregate status).</summary>
    public event EventHandler? SessionsChanged;

    /// <summary>Gets all active sessions as a snapshot.</summary>
    public IReadOnlyList<SessionInfo> GetAllSessions() => _sessions.Values.ToList();

    /// <summary>Gets the number of active sessions.</summary>
    public int SessionCount => _sessions.Count;

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

    /// <summary>Remove a session (session ended).</summary>
    public void RemoveSession(string sessionId)
    {
        StopBusyTimer(sessionId);
        StopSubagentTimer(sessionId);

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
