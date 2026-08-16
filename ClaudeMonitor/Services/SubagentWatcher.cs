using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
/// <c>description</c>. Subagent files are NOT deleted when the subagent ends.
///
/// <para><b>Phase 2 (TASKS.md §5.2):</b> activity is now judged by
/// <see cref="SubagentTailer"/>, which incrementally parses each
/// <c>agent-&lt;id&gt;.jsonl</c> and derives a fine-grained
/// <see cref="SubagentState"/> (Thinking/ToolRunning/WaitingApi/Completed/Failed)
/// from the entries. The tailer is the authoritative source; the previous
/// 20s last-line-timestamp window is retained only as a fallback for the case
/// where the tailer has not yet been attached (e.g. CC-Pulse launched
/// mid-subagent) or the file disappeared.</para>
/// </summary>
public class SubagentWatcher : IDisposable
{
    /// <summary>
    /// Fallback stale window (seconds). A subagent whose jsonl has had no new
    /// line within this window AND whose tailer has not reported a state is
    /// considered ended. This only governs the fallback path; the tailer's
    /// <see cref="SubagentState.Completed"/>/Failed detection is immediate.
    /// 20s covers a subagent's think/long-tool gap while keeping fallback
    /// residue short.
    /// </summary>
    private const int FallbackStaleSeconds = 20;

    /// <summary>Poll interval for re-scanning each session's subagents directory.</summary>
    private const int PollIntervalMs = 2000;

    private const string SubagentsFolderName = "subagents";

    private static readonly string ClaudeProjectsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "projects");

    private readonly SessionManager _sessionManager;
    private readonly SubagentTailer _tailer;
    private readonly Dictionary<string, string> _sessionProjects = new();
    private System.Threading.Timer? _pollTimer;
    private readonly object _lock = new();
    private bool _disposed;

    public SubagentWatcher(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
        _tailer = new SubagentTailer();
        _tailer.StateChanged += OnSubagentStateChanged;
    }

    /// <summary>The subagent tailer (started by <see cref="Start"/>).</summary>
    public SubagentTailer Tailer => _tailer;

    /// <summary>Start watching. Call once at app startup.</summary>
    public void Start()
    {
        if (_pollTimer != null) return;
        _tailer.Start();
        _pollTimer = new System.Threading.Timer(PollAllSessions, null,
            PollIntervalMs, PollIntervalMs);
    }

    /// <summary>
    /// Register a session so its subagents directory is polled. Called when a
    /// session starts (and its project path is known).
    /// </summary>
    public void RegisterSession(string sessionId, string projectPath)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(projectPath))
            return;
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
        // Deactivation of individual subagent tails is handled by
        // SessionManager.RemoveSession (which knows the agent ids on the
        // session). Nothing more to do here.
    }

    /// <summary>
    /// Stop tailing a specific subagent (e.g. when its row is removed by the
    /// SubagentStop hook or when the session ends). Called by SessionManager.
    /// </summary>
    public void DeactivateSubagent(string agentId)
    {
        _tailer.DeactivateSubagent(agentId);
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
    /// with the current set of active subagents. For each discovered
    /// <c>agent-&lt;id&gt;.jsonl</c>, activate the <see cref="SubagentTailer"/>
    /// so its state is derived authoritatively from the transcript content.
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

        var now = DateTime.UtcNow;
        var active = new List<SubagentInfo>();

        foreach (var jsonlPath in Directory.EnumerateFiles(subagentsDir, "agent-*.jsonl"))
        {
            var agentId = ExtractAgentId(Path.GetFileName(jsonlPath));

            // Fallback activity check: if the file's last line timestamp is
            // stale beyond the window, the subagent ended before the tailer
            // could observe a turn-end (e.g. CC-Pulse launched after the
            // subagent finished). Deactivate any lingering tail and skip it so
            // it is not reported active. This avoids tailing dead files.
            //
            // BUT this only applies when the tailer is NOT already tailing this
            // agent. Once the tailer has attached, it is the authoritative state
            // source (§5.2) and must be allowed to derive Completed/Failed on
            // its own 40s silence threshold. Letting this 20s fallback preempt
            // the tailer would Dispose the tail before it ever derives a
            // terminal state — so a subagent paused on a permission approval
            // (file legitimately silent > 20s while the user decides) would
            // have its tail killed, then re-activated as Pending when it
            // resumes, producing the "disappear → re-spawning" flicker. The
            // tailer's terminal memory + the escape hatch below handle the
            // false-terminal case; this fallback is only for the
            // never-attached (already-dead-before-launch) case.
            var lastActivity = ReadLastActivityUtc(jsonlPath);
            if (!_tailer.IsTailing(agentId) &&
                (lastActivity is null ||
                 (now - lastActivity.Value).TotalSeconds > FallbackStaleSeconds))
            {
                _tailer.DeactivateSubagent(agentId);
                continue;
            }

            // Skip a subagent the tailer has already judged terminal. Without
            // this, a false terminal (a long think/act gap crossing
            // IdleToCompletedSeconds while the subagent was still running)
            // flickers: the tailer derives Completed → the row is removed and
            // the tail deactivated → this poll sees a still-recent last-line
            // timestamp and re-activates the tail + re-adds the row as Pending
            // → "disappear → re-spawning" flicker.
            //
            // Escape hatch (guess verdicts only): if the jsonl grew PAST a
            // guess terminal verdict (a new line was written after termAt), the
            // subagent was still running — the verdict was false. Clear the
            // memory and let the normal activation path run so the row
            // re-appears and the tail re-derives state from the new line (not
            // stuck on Pending). Authoritative verdicts (task-notification /
            // agents_killed) are NEVER recovered — the subagent is truly done,
            // and a later jsonl growth is a new invocation, not a recovery.
            //
            // Content guard (double insurance): even for a guess verdict, do
            // not revive if the new line that grew the file is a terminating
            // user message ("[Request interrupted by user]") — that is a real
            // end, not a recovery. Handles the edge case where the
            // agents_killed signal is lost or lags behind the subagent
            // transcript write.
            if (_tailer.IsTerminal(agentId))
            {
                if (_tailer.IsAuthoritativeTerminal(agentId))
                {
                    // Authoritative terminal — never revive. Deactivate any
                    // lingering tail and keep the row gone.
                    _tailer.DeactivateSubagent(agentId);
                    continue;
                }

                bool revived = false;
                if (lastActivity is not null &&
                    _tailer.TryGetTerminalAt(agentId, out var termAt) &&
                    lastActivity.Value > termAt)
                {
                    // File grew after the guess verdict. Check the new line's
                    // content before reviving: an interrupt line is a real end.
                    if (!IsInterruptLine(jsonlPath, termAt))
                    {
                        _tailer.ClearTerminal(agentId);
                        revived = true;
                        FileLogger.Info(
                            $"subagent terminal override (file grew) agent={agentId} " +
                            $"termAt={termAt:o} lastActivity={lastActivity.Value:o}");
                    }
                    else
                    {
                        FileLogger.Info(
                            $"subagent terminal kept (interrupt line) agent={agentId} " +
                            $"termAt={termAt:o} lastActivity={lastActivity.Value:o}");
                    }
                }

                if (!revived)
                {
                    // True terminal, or file has not grown past the verdict, or
                    // the new line was an interrupt → keep the row gone.
                    _tailer.DeactivateSubagent(agentId);
                    continue;
                }
            }

            var meta = ReadMeta(subagentsDir, agentId);

            // Activate the tailer for this active subagent file. The tailer is
            // idempotent — re-activating an already-tailed path is a no-op.
            // On first activation the offset is set to EOF, so only NEW
            // appends drive state changes; the fallback mtime check above
            // covers the gap before the first new line arrives.
            _tailer.ActivateSubagent(agentId, jsonlPath);

            var info = new SubagentInfo
            {
                AgentId = agentId,
                AgentType = meta.AgentType,
                Description = meta.Description,
                DisplayName = ChooseDisplayName(meta.AgentType, meta.Description),
                LastActivityUtc = lastActivity ?? DateTime.UtcNow,
            };
            active.Add(info);
        }

        _sessionManager.UpdateSubagents(sessionId, active);
    }

    /// <summary>
    /// Handler for <see cref="SubagentTailer.StateChanged"/>: forward the
    /// fine-grained state to the SessionManager so the matching
    /// <see cref="SubagentInfo"/> is updated and the UI refreshes.
    /// </summary>
    private void OnSubagentStateChanged(object? sender, SubagentStateEventArgs e)
    {
        _sessionManager.UpdateSubagentState(e.AgentId, e.State, e.ActiveToolName, e.Source);
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

    /// <summary>
    /// Whether the first complete jsonl line written after
    /// <paramref name="afterUtc"/> is a terminating user-interrupt message
    /// (<c>[Request interrupted by user]</c>). Used by the escape hatch's
    /// content guard: a guess terminal verdict should NOT be revived when the
    /// new line that grew the file is an interrupt, because that is a real end,
    /// not a recovery. Reads forward from a small window near the file's end
    /// and inspects only the lines whose timestamp is later than
    /// <paramref name="afterUtc"/>. Returns false if no such line is found or
    /// it is not an interrupt.
    /// </summary>
    private static bool IsInterruptLine(string jsonlPath, DateTime afterUtc)
    {
        try
        {
            using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length == 0) return false;

            // Scan the whole file is wasteful for large transcripts, but
            // subagent files are small (a few hundred lines at most). Read all
            // text and inspect lines whose timestamp exceeds afterUtc.
            fs.Seek(0, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line) || line[0] != '{') continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("timestamp", out var tsProp) ||
                        tsProp.ValueKind != JsonValueKind.String) continue;
                    if (!DateTime.TryParse(tsProp.GetString(),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal
                        | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var dt))
                        continue;
                    if (dt.ToUniversalTime() <= afterUtc) continue;

                    // First line after the verdict. A user entry whose content
                    // is the interrupt sentinel string → real end.
                    if (!root.TryGetProperty("type", out var tProp) ||
                        tProp.GetString() != "user") return false;
                    if (!root.TryGetProperty("message", out var msg) ||
                        msg.ValueKind != JsonValueKind.Object) return false;
                    if (!msg.TryGetProperty("content", out var content) ||
                        content.ValueKind != JsonValueKind.String) return false;
                    return content.GetString()
                        ?.Contains("[Request interrupted by user]",
                            StringComparison.Ordinal) == true;
                }
                catch (JsonException)
                {
                    continue;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Read the timestamp of the last complete line of an agent jsonl file,
    /// returned as UTC. Used only for the fallback stale-window check; the
    /// authoritative state comes from <see cref="SubagentTailer"/>. Each line
    /// carries a top-level <c>timestamp</c> field (ISO 8601, UTC). Scans
    /// backward from the end of the file in chunks to locate the last
    /// complete line. Returns null if no parseable timestamped line is found.
    /// </summary>
    private static DateTime? ReadLastActivityUtc(string jsonlPath)
    {
        try
        {
            using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var length = fs.Length;
            if (length == 0) return null;

            const int chunkSize = 8192;
            byte[] collected = Array.Empty<byte>();
            var pos = length;
            int newlineCount = 0;

            while (pos > 0)
            {
                var readLen = (int)Math.Min(chunkSize, pos);
                pos -= readLen;
                fs.Seek(pos, SeekOrigin.Begin);
                var chunk = new byte[readLen];
                var read = fs.Read(chunk, 0, readLen);
                if (read == 0) break;

                if (collected.Length > 0)
                {
                    var combined = new byte[read + collected.Length];
                    Buffer.BlockCopy(chunk, 0, combined, 0, read);
                    Buffer.BlockCopy(collected, 0, combined, read, collected.Length);
                    collected = combined;
                }
                else
                {
                    collected = chunk;
                }

                for (int i = read - 1; i >= 0; i--)
                {
                    if (chunk[i] == '\n')
                    {
                        newlineCount++;
                        if (newlineCount >= 2)
                        {
                            return ParseTimestampFromTail(collected, i + 1);
                        }
                    }
                }

                if (collected.Length > 256 * 1024) break;
            }

            return ParseTimestampFromTail(collected, 0);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Given a byte buffer and a start offset, extract the line beginning at
    /// <paramref name="start"/> (up to the next '\n' or end of buffer), parse
    /// it as JSON, and return its <c>timestamp</c> as UTC. Returns null if the
    /// line is empty, not JSON, or has no parseable timestamp.
    /// </summary>
    private static DateTime? ParseTimestampFromTail(byte[] buffer, int start)
    {
        int end = start;
        while (end < buffer.Length && buffer[end] != '\n')
            end++;

        if (end <= start) return null;

        var line = Encoding.UTF8.GetString(buffer, start, end - start).Trim();
        if (line.Length == 0 || line[0] != '{') return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("timestamp", out var tsProp)
                && tsProp.ValueKind == JsonValueKind.String
                && DateTime.TryParse(tsProp.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                    | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var dt))
            {
                return dt.ToUniversalTime();
            }
        }
        catch
        {
            // Malformed line — no timestamp available.
        }
        return null;
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

        if (!string.IsNullOrWhiteSpace(description))
        {
            var summary = description.Trim();
            return summary.Length <= 40 ? summary : summary[..37] + "…";
        }

        return string.IsNullOrEmpty(agentType) ? "subagent" : agentType;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer?.Dispose();
        _pollTimer = null;
        _tailer.StateChanged -= OnSubagentStateChanged;
        _tailer.Dispose();
        lock (_lock)
        {
            _sessionProjects.Clear();
        }
    }
}
