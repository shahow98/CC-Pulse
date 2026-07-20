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
        set => SetField(ref _status, value);
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
}
