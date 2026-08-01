using System;
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
