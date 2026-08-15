using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClaudeMonitor.Models;

/// <summary>
/// Fine-grained internal state of a subagent (TASKS.md §5.1). Derived by
/// <see cref="Services.SubagentTailer"/> from the subagent's own transcript
/// (<c>agent-&lt;id&gt;.jsonl</c>), which is the authoritative source. The
/// coarse <c>Working/Idle</c> view shown in the UI is derived from this:
/// <see cref="Pending"/>, <see cref="Thinking"/>, <see cref="ToolRunning"/>,
/// and <see cref="WaitingApi"/> all render as "Working" (the subagent is not
/// done); <see cref="Completed"/> and <see cref="Failed"/> render as "Idle"
/// (the subagent has finished).
/// </summary>
public enum SubagentState
{
    /// <summary>
    /// The Task/Agent tool_use was observed in the main transcript, but the
    /// subagent's own transcript file does not yet exist (or has no
    /// parseable entries). The subagent is being spawned.
    /// </summary>
    Pending,

    /// <summary>
    /// The subagent's last transcript entry is a user/assistant text message
    /// with no unpaired tool_use. The subagent is reasoning or waiting for
    /// the model to start the next action.
    /// </summary>
    Thinking,

    /// <summary>
    /// The subagent's transcript has at least one tool_use without a matching
    /// tool_result. The subagent is actively executing a tool.
    /// </summary>
    ToolRunning,

    /// <summary>
    /// The subagent's last entry is a user message and more than
    /// <c>WaitingApiThresholdSeconds</c> have elapsed with no assistant
    /// response. The subagent is waiting for the API (rate limit, network).
    /// </summary>
    WaitingApi,

    /// <summary>
    /// The subagent's transcript has a turn-end signal (system
    /// stop_hook_summary) with no error, or the main session received the
    /// matching tool_result. The subagent has finished successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// The subagent's turn-end signal carried <c>is_error == true</c>, or the
    /// main session received an error tool_result for this subagent's
    /// parent tool_use. The subagent failed.
    /// </summary>
    Failed,
}

/// <summary>
/// Holds state for a single active subagent (spawned via the Agent/Task tool).
/// The subagent watcher maintains one instance per detected subagent; the UI
/// aggregates them into a single working/idle indicator rather than showing
/// each individually.
/// </summary>
public class SubagentInfo : INotifyPropertyChanged
{
    private string _agentId = string.Empty;
    private string _displayName = string.Empty;
    private string _agentType = string.Empty;
    private string _description = string.Empty;
    private SubagentState _state = SubagentState.Pending;
    private string _activeToolName = string.Empty;
    private StateSource _stateSource = StateSource.Hook;
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    /// <summary>Unique agent identifier (matches the agent-*.jsonl filename).</summary>
    public string AgentId
    {
        get => _agentId;
        set => SetField(ref _agentId, value);
    }

    /// <summary>
    /// Human-readable name shown in the UI. Chosen intelligently from
    /// <see cref="AgentType"/> and <see cref="Description"/>: custom agents
    /// show their type name; built-in general-purpose agents show the task
    /// description summary.
    /// </summary>
    public string DisplayName
    {
        get => _displayName;
        set => SetField(ref _displayName, value);
    }

    /// <summary>Raw agent type from meta.json (e.g. general-purpose, Explore, code-reviewer).</summary>
    public string AgentType
    {
        get => _agentType;
        set => SetField(ref _agentType, value);
    }

    /// <summary>Raw task description from meta.json.</summary>
    public string Description
    {
        get => _description;
        set => SetField(ref _description, value);
    }

    /// <summary>
    /// Fine-grained internal state (TASKS.md §5.1). Authoritatively derived
    /// from the subagent transcript by <see cref="Services.SubagentTailer"/>.
    /// Until the tailer observes the subagent file, this stays
    /// <see cref="SubagentState.Pending"/>. The UI maps this to a localized
    /// label and a working/idle color.
    /// </summary>
    public SubagentState State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

    /// <summary>
    /// Name of the tool currently executing in the subagent (when
    /// <see cref="State"/> is <see cref="SubagentState.ToolRunning"/>), empty
    /// otherwise. Reported in the output structure (TASKS.md §6
    /// <c>tool_name</c>).
    /// </summary>
    public string ActiveToolName
    {
        get => _activeToolName;
        set => SetField(ref _activeToolName, value);
    }

    /// <summary>
    /// Which source determined <see cref="State"/> (TASKS.md §6
    /// <c>source</c>). <see cref="StateSource.Transcript"/> once the
    /// subagent tailer has parsed at least one entry; <see cref="StateSource.Hook"/>
    /// while only the spawning hook has been seen.
    /// </summary>
    public StateSource StateSource
    {
        get => _stateSource;
        set => SetField(ref _stateSource, value);
    }

    /// <summary>
    /// UTC timestamp of the most recent transcript activity observed for this
    /// subagent. Used by the watcher's stale-window fallback (when the tailer
    /// has not yet been attached or the file disappeared) and for
    /// <see cref="SubagentState.WaitingApi"/> derivation.
    /// </summary>
    public DateTime LastActivityUtc
    {
        get => _lastActivityUtc;
        set => SetField(ref _lastActivityUtc, value);
    }

    /// <summary>
    /// True when the subagent is in any non-terminal working state
    /// (Pending/Thinking/ToolRunning/WaitingApi). Drives the subagent
    /// indicator color (red while working) and the aggregate main-agent
    /// Busy derivation (main is Busy while any subagent works).
    /// </summary>
    public bool IsWorking =>
        _state == SubagentState.Pending
        || _state == SubagentState.Thinking
        || _state == SubagentState.ToolRunning
        || _state == SubagentState.WaitingApi;

    /// <summary>
    /// Confidence (0-1) reported in the output structure (TASKS.md §6).
    /// Transcript-derived state is high confidence (0.95); hook-only Pending
    /// state is lower (0.7).
    /// </summary>
    public double Confidence =>
        _stateSource == StateSource.Transcript ? 0.95 : 0.7;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        // IsWorking and Confidence derive from State/StateSource, so notify
        // their bindings whenever the inputs change.
        if (propertyName == nameof(State) || propertyName == nameof(StateSource))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsWorking)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Confidence)));
        }
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
