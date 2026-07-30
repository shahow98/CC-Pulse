using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ClaudeMonitor.Models;

namespace ClaudeMonitor.Services;

/// <summary>
/// Monitors each active session's <c>subagents/</c> directory on disk to
/// detect subagent activity authoritatively. This supplements the hook-based
/// detection (which misses subagents when the spawning tool is named "Task"
/// instead of "Agent", and whose SubagentStop event is unreliable).
///
/// For each session, Claude Code writes one <c>agent-&lt;id&gt;.jsonl</c> per
/// subagent under <c>~/.claude/projects/&lt;encodedProj&gt;/&lt;sessionId&gt;/subagents/</c>,
/// alongside an <c>agent-&lt;id&gt;.meta.json</c> carrying <c>agentType</c> and
/// <c>description</c>. Subagent files are NOT deleted when the subagent ends,
/// so activity is judged by the jsonl file's last-write time: if it was
/// modified within the recent window, the subagent is still working.
/// </summary>
public class SubagentWatcher : IDisposable
{
    /// <summary>
    /// A subagent jsonl whose last-write time falls within this window (relative
    /// to now) is considered still active. Subagents append lines while working
    /// and stop appending when done, so a stale mtime means the subagent ended.
    /// </summary>
    private const int ActiveWindowSeconds = 15;

    /// <summary>Poll interval for re-scanning each session's subagents directory.</summary>
    private const int PollIntervalMs = 4000;

    private const string SubagentsFolderName = "subagents";

    private static readonly string ClaudeProjectsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "projects");

    private readonly SessionManager _sessionManager;
    private readonly Dictionary<string, string> _sessionProjects = new();
    private System.Threading.Timer? _pollTimer;
    private readonly object _lock = new();
    private bool _disposed;

    public SubagentWatcher(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    /// <summary>Start watching. Call once at app startup.</summary>
    public void Start()
    {
        if (_pollTimer != null) return;
        _pollTimer = new System.Threading.Timer(PollAllSessions, null,
            PollIntervalMs, PollIntervalMs);
    }

    /// <summary>
    /// Register a session so its subagents directory is polled. Called when a
    /// session starts (and its project path is known).
    /// </summary>
    public void RegisterSession(string sessionId, string projectPath)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(projectPath)) return;
        lock (_lock)
        {
            _sessionProjects[sessionId] = projectPath;
        }
        // Trigger an immediate scan for this session so the subagent row
        // appears without waiting for the next poll tick.
        _ = ThreadPool.QueueUserWorkItem(_ => PollSession(sessionId, projectPath));
    }

    /// <summary>Unregister a session and clear any subagents it had.</summary>
    public void UnregisterSession(string sessionId)
    {
        lock (_lock)
        {
            _sessionProjects.Remove(sessionId);
        }
        // Clearing the subagent list is handled by SessionManager.RemoveSession
        // (the session itself is gone), so nothing more to do here.
    }

    private void PollAllSessions(object? state)
    {
        List<KeyValuePair<string, string>> snapshot;
        lock (_lock)
        {
            snapshot = _sessionProjects.ToList();
        }

        foreach (var (sessionId, projectPath) in snapshot)
        {
            try
            {
                PollSession(sessionId, projectPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SubagentWatcher poll error for {sessionId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Scan one session's subagents directory and update the SessionManager
    /// with the current set of active subagents.
    /// </summary>
    private void PollSession(string sessionId, string projectPath)
    {
        var subagentsDir = ResolveSubagentsDir(projectPath, sessionId);
        if (subagentsDir is null || !Directory.Exists(subagentsDir))
        {
            // No subagents directory yet — ensure any stale subagent state is cleared.
            _sessionManager.UpdateSubagents(sessionId, Array.Empty<SubagentInfo>());
            return;
        }

        var now = DateTime.Now;
        var active = new List<SubagentInfo>();

        foreach (var jsonlPath in Directory.EnumerateFiles(subagentsDir, "agent-*.jsonl"))
        {
            var lastWrite = File.GetLastWriteTime(jsonlPath);
            if ((now - lastWrite).TotalSeconds > ActiveWindowSeconds)
                continue; // stale — subagent already ended

            var agentId = ExtractAgentId(Path.GetFileName(jsonlPath));
            var meta = ReadMeta(subagentsDir, agentId);
            var info = new SubagentInfo
            {
                AgentId = agentId,
                AgentType = meta.AgentType,
                Description = meta.Description,
                DisplayName = ChooseDisplayName(meta.AgentType, meta.Description),
            };
            active.Add(info);
        }

        _sessionManager.UpdateSubagents(sessionId, active);
    }

    /// <summary>
    /// Resolve the subagents directory for a session. Claude Code encodes the
    /// project path into the projects folder name by replacing ':', '\', '/'
    /// with '-'. Returns null if the project path cannot be encoded.
    /// </summary>
    private static string? ResolveSubagentsDir(string projectPath, string sessionId)
    {
        var encoded = EncodeProjectPath(projectPath);
        if (encoded is null) return null;
        return Path.Combine(ClaudeProjectsDir, encoded, sessionId, SubagentsFolderName);
    }

    /// <summary>
    /// Encode a project path the way Claude Code does: replace drive separator
    /// and path separators with '-'. e.g. "C:\Users\foo\bar" -> "C--Users-foo-bar".
    /// </summary>
    private static string? EncodeProjectPath(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        var chars = projectPath.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] == ':' || chars[i] == '\\' || chars[i] == '/')
                chars[i] = '-';
        }
        return new string(chars);
    }

    /// <summary>Extract the agent id from an "agent-&lt;id&gt;.jsonl" filename.</summary>
    private static string ExtractAgentId(string fileName)
    {
        // "agent-a031050c07014db13.jsonl" -> "a031050c07014db13"
        const string prefix = "agent-";
        if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            fileName = fileName[prefix.Length..];
        const string suffix = ".jsonl";
        if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            fileName = fileName[..^suffix.Length];
        return fileName;
    }

    /// <summary>Read the matching agent-&lt;id&gt;.meta.json, if present.</summary>
    private static (string AgentType, string Description) ReadMeta(string dir, string agentId)
    {
        var metaPath = Path.Combine(dir, $"agent-{agentId}.meta.json");
        if (!File.Exists(metaPath))
            return (string.Empty, string.Empty);

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            var agentType = doc.RootElement.TryGetProperty("agentType", out var at) ? at.GetString() ?? "" : "";
            var description = doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            return (agentType, description);
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// Choose the display name shown in the UI:
    /// - Custom agents (non-built-in type) show the agent type name.
    /// - Built-in general-purpose agents show the task description summary.
    /// Built-in types: general-purpose, Explore, Plan, statusline-setup.
    /// </summary>
    private static string ChooseDisplayName(string agentType, string description)
    {
        var builtIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "general-purpose", "Explore", "Plan", "statusline-setup",
        };

        if (!string.IsNullOrEmpty(agentType) && !builtIn.Contains(agentType))
            return agentType;

        // Built-in or unknown type — prefer the description summary.
        if (!string.IsNullOrWhiteSpace(description))
        {
            var summary = description.Trim();
            // Keep the row compact; truncate long descriptions.
            return summary.Length <= 40 ? summary : summary[..37] + "…";
        }

        // Nothing to show — fall back to the type, or a generic label.
        return string.IsNullOrEmpty(agentType) ? "subagent" : agentType;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer?.Dispose();
        _pollTimer = null;
        lock (_lock)
        {
            _sessionProjects.Clear();
        }
    }
}
