using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

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
    private string _subagentDescription = string.Empty;
    private bool _isWorking;

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
    /// True when a subagent (spawned via the Agent tool) is currently running
    /// in this session. While active, the main agent is waiting, so the main
    /// status shows Idle and a separate subagent row shows Working.
    /// </summary>
    public bool SubagentActive
    {
        get => _subagentActive;
        set
        {
            if (SetField(ref _subagentActive, value))
            {
                // SubagentWorking derives from SubagentActive, notify its binding
                OnPropertyChanged(nameof(SubagentWorking));
                RefreshIsWorking();
            }
        }
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
    public bool SubagentWorking => _subagentActive;

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
