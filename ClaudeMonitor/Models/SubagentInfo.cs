using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClaudeMonitor.Models;

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
