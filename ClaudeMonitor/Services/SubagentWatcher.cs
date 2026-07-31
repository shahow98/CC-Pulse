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
/// <c>description</c>. Subagent files are NOT deleted when the subagent ends,
/// so activity is judged by the jsonl file's last-write time: if it was
/// modified within the recent window, the subagent is still working.
/// </summary>
public class SubagentWatcher : IDisposable
{
    /// <summary>
    /// A subagent whose last jsonl line timestamp falls within this window
    /// (relative to now) is considered still active. Subagents append lines
    /// while working and stop appending when done, so a stale last-timestamp
    /// means the subagent ended.
    ///
    /// This is now a FALLBACK: the SubagentStop hook removes a finished
    /// subagent's row immediately (via SessionManager.RemoveSubagent). This
    /// window only governs the case where SubagentStop does not fire (some
    /// modes don't emit it). 20s covers a subagent's think/long-tool gap
    /// (30s+ with no new line is rare) while keeping the fallback residue
    /// short. The watcher polls every 2s using the precise per-line timestamp.
    /// </summary>
    private const int ActiveWindowSeconds = 20;

    /// <summary>Poll interval for re-scanning each session's subagents directory.</summary>
    private const int PollIntervalMs = 2000;

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

        var now = DateTime.UtcNow;
        var active = new List<SubagentInfo>();

        foreach (var jsonlPath in Directory.EnumerateFiles(subagentsDir, "agent-*.jsonl"))
        {
            // Judge activity by the timestamp on the LAST jsonl line (UTC,
            // precise to the millisecond) rather than the file's last-write
            // time. Windows NTFS mtime for append-heavy files is cached and
            // can lag or stall, which caused subagents to vanish prematurely
            // while still working. Each line carries a `timestamp` field, so
            // the last line's timestamp is the authoritative "last activity".
            var lastActivity = ReadLastActivityUtc(jsonlPath);
            if (lastActivity is null || (now - lastActivity.Value).TotalSeconds > ActiveWindowSeconds)
                continue; // stale — subagent already ended (or no parseable line)

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

    /// <summary>
    /// Read the timestamp of the last complete line of an agent jsonl file,
    /// returned as UTC. Each line is a JSON object with a top-level
    /// <c>timestamp</c> field (ISO 8601, UTC, e.g. "2026-07-31T09:57:23.479Z").
    /// This is far more reliable than <see cref="File.GetLastWriteTime"/>, whose
    /// mtime on Windows is cached for append-heavy writes and can lag or stall.
    ///
    /// A single jsonl line can be tens of KB (assistant messages with large
    /// attachments), so this scans backward from the end of the file in chunks
    /// until it locates the last newline that terminates a complete line, then
    /// parses that line. Returns null if no parseable timestamped line is found.
    /// </summary>
    private static DateTime? ReadLastActivityUtc(string jsonlPath)
    {
        try
        {
            using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var length = fs.Length;
            if (length == 0) return null;

            // Scan backward in chunks to find the start of the last complete
            // line. The file ends with '\n' (after the last line), so the last
            // complete line is the text between the second-to-last '\n' and the
            // final '\n'. We collect bytes from the end until we have seen at
            // least one '\n' that is not the very last byte.
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

                // Prepend this chunk to what we've collected from the tail.
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

                // Count newlines in the freshly read region (the first `read`
                // bytes of `collected`). Stop once we have a complete line.
                for (int i = read - 1; i >= 0; i--)
                {
                    if (chunk[i] == '\n')
                    {
                        newlineCount++;
                        if (newlineCount >= 2)
                        {
                            // The byte after this '\n' begins the last complete line.
                            return ParseTimestampFromTail(collected, i + 1);
                        }
                    }
                }

                // Safety cap: never read more than ~256 KB looking for a line.
                if (collected.Length > 256 * 1024) break;
            }

            // Reached the start of file with fewer than 2 newlines: the whole
            // file is one line (or the first line is the last complete line).
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
        // Find the end of the line starting at `start`.
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
