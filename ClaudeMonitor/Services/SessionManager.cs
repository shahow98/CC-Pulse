using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ClaudeMonitor.Models;

namespace ClaudeMonitor.Services;

/// <summary>
/// Thread-safe manager for all active Claude Code sessions.
/// Maintains session state and raises events when status changes.
/// </summary>
public class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();

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

    /// <summary>Update a session's status.</summary>
    public void UpdateStatus(string sessionId, SessionStatus newStatus)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        var oldStatus = session.Status;
        if (oldStatus == newStatus) return;

        session.Status = newStatus;
        session.LastUpdated = DateTime.Now;

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

    public void Dispose()
    {
        _sessions.Clear();
    }
}
