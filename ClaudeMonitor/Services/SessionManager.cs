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

    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly ConcurrentDictionary<string, System.Threading.Timer> _busyTimers = new();

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
    /// </summary>
    public void ResetBusyTimeout(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session) && session.Status == SessionStatus.Busy)
        {
            StartOrResetBusyTimer(sessionId);
        }
    }

    /// <summary>Remove a session (session ended).</summary>
    public void RemoveSession(string sessionId)
    {
        StopBusyTimer(sessionId);

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

    public void Dispose()
    {
        // Dispose all watchdog timers
        foreach (var timer in _busyTimers.Values)
            timer.Dispose();
        _busyTimers.Clear();

        _sessions.Clear();
    }
}
