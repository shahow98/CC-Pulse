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
/// Includes timer-based detection of Interactive (waiting for user) state.
/// </summary>
public class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly ConcurrentDictionary<string, System.Threading.Timer> _interactiveTimers = new();
    private readonly TimeSpan _interactiveTimeout = TimeSpan.FromSeconds(10);

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
    /// Red > Yellow > Green. Returns Idle if no sessions.
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

    /// <summary>Update a session's status. Starts interactive timer if transitioning to Idle from Busy.</summary>
    public void UpdateStatus(string sessionId, SessionStatus newStatus)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        var oldStatus = session.Status;
        if (oldStatus == newStatus) return;

        session.Status = newStatus;
        session.LastUpdated = DateTime.Now;

        // Cancel any pending interactive timer
        if (_interactiveTimers.TryRemove(sessionId, out var timer))
        {
            timer.Dispose();
        }

        // When transitioning from Busy to Idle, start a timer to detect Interactive state.
        // If the user doesn't submit a new prompt within the timeout, the session
        // is likely waiting for user input → switch to Interactive (red).
        if (oldStatus == SessionStatus.Busy && newStatus == SessionStatus.Idle)
        {
            StartInteractiveTimer(sessionId);
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

    /// <summary>Remove a session (session ended).</summary>
    public void RemoveSession(string sessionId)
    {
        if (_interactiveTimers.TryRemove(sessionId, out var timer))
        {
            timer.Dispose();
        }

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
    /// Starts a timer that will transition the session to Interactive after the timeout.
    /// Called when a session goes from Busy → Idle, as a heuristic for detecting
    /// that Claude is waiting for user input.
    /// </summary>
    private void StartInteractiveTimer(string sessionId)
    {
        var timer = new System.Threading.Timer(_ =>
        {
            // If the session is still Idle after the timeout, assume it's waiting for user input
            if (_sessions.TryGetValue(sessionId, out var session) && session.Status == SessionStatus.Idle)
            {
                UpdateStatus(sessionId, SessionStatus.Interactive);
            }
        }, null, _interactiveTimeout, Timeout.InfiniteTimeSpan);

        _interactiveTimers[sessionId] = timer;
    }

    public void Dispose()
    {
        foreach (var timer in _interactiveTimers.Values)
        {
            timer.Dispose();
        }
        _interactiveTimers.Clear();
        _sessions.Clear();
    }
}
