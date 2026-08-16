using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClaudeMonitor.Models;

namespace ClaudeMonitor.Services;

/// <summary>
/// Tails each active subagent's transcript JSONL file
/// (<c>~/.claude/projects/&lt;encoded&gt;/&lt;sessionId&gt;/subagents/agent-&lt;id&gt;.jsonl</c>)
/// and derives the subagent's fine-grained internal state (TASKS.md §5.2)
/// from the entries it observes. The subagent transcript is the authoritative
/// source for subagent state, just as the main transcript is authoritative
/// for the main agent (§4).
///
/// For each tailed file we maintain:
///  - a per-file read offset (incremental tail, no historical replay);
///  - the set of unpaired <c>tool_use</c> ids (tool_use without a matching
///    tool_result) — non-empty means <see cref="SubagentState.ToolRunning"/>;
///  - the timestamp of the last <c>user</c> entry and the last
///    <c>assistant</c> entry, to detect <see cref="SubagentState.WaitingApi"/>
///    (user message with no assistant response for &gt; 10s);
///  - whether a turn-end signal (<c>system.stop_hook_summary</c>) was seen
///    and whether it carried <c>is_error</c>.
///
/// A <see cref="FileSystemWatcher"/> catches appends in real time with a
/// 500ms polling fallback (required on Windows). Files are opened with
/// <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/> so
/// Claude Code's write lock does not block reads.
/// </summary>
public class SubagentTailer : IDisposable
{
    /// <summary>
    /// Polling interval for the append-detection fallback (§2.2). Matches the
    /// main <see cref="TranscriptTailer"/> cadence.
    /// </summary>
    private const int PollIntervalMs = 500;

    /// <summary>
    /// Debounce for FSWatcher events (§7): coalesce rapid notifications.
    /// </summary>
    private const int WatcherDebounceMs = 100;

    /// <summary>
    /// A single JSONL line larger than this is skipped with a warning (§7).
    /// </summary>
    private const long MaxLineBytes = 1024 * 1024;

    /// <summary>
    /// If the last <c>user</c> entry is older than this (in seconds) relative
    /// to now and no <c>assistant</c> entry followed it, the subagent is
    /// considered to be waiting for the API (§5.2).
    /// </summary>
    private const int WaitingApiThresholdSeconds = 10;

    /// <summary>
    /// Silence-to-terminal threshold (seconds). Claude Code does not write a
    /// <c>system.stop_hook_summary</c> (or any <c>system</c> entry) to the
    /// subagent's own transcript, so the <c>_turnEnded</c> path never fires.
    /// When the subagent has had activity (an assistant entry), has no unpaired
    /// tool_use, and no new line has arrived for this long, we infer the
    /// subagent has finished and derive <see cref="SubagentState.Completed"/>.
    ///
    /// <para>120s covers the long think/summary-generation gaps observed in
    /// practice (a 99.8s gap was seen while a user hesitated over a permission
    /// approval; a 221s gap occurred while a subagent generated a long summary).
    /// A silence-derived terminal is a <b>guess</b> (see
    /// <see cref="TerminalVerdict.IsAuthoritative"/>) — the escape hatch in
    /// <see cref="SubagentWatcher"/> recovers a false guess when the jsonl
    /// grows past the verdict. Authoritative terminals (task-notification /
    /// agents_killed from the main transcript) are never recovered, so raising
    /// this threshold only reduces how often a still-running subagent's row
    /// briefly disappears before the escape hatch re-adds it.</para>
    /// </summary>
    private const int IdleToCompletedSeconds = 120;

    private readonly ConcurrentDictionary<string, SubagentFileTail> _tails = new();

    /// <summary>
    /// Terminal-state memory: agent ids the tailer has already derived into
    /// <see cref="SubagentState.Completed"/> or <see cref="SubagentState.Failed"/>,
    /// mapped to the verdict (UTC moment + whether it is authoritative).
    /// Independent of the tail lifecycle — <see cref="DeactivateSubagent"/> does
    /// NOT clear an entry, so the watcher can skip re-activating a subagent
    /// whose row was removed on a (possibly false) terminal verdict.
    ///
    /// <para><b>Authoritative vs guess:</b> a verdict set by
    /// <see cref="MarkAuthoritativeTerminal"/> (task-notification /
    /// agents_killed from the main transcript) is authoritative and is NEVER
    /// recovered — the subagent is truly done. A verdict set by the
    /// silence-to-terminal path (<see cref="IdleToCompletedSeconds"/>) is a
    /// guess: the escape hatch in <see cref="SubagentWatcher"/> recovers it
    /// when the jsonl grows past <c>termAt</c> (a new line was written after
    /// the verdict → the subagent was still running). Cleared wholesale by
    /// <see cref="ClearAllTerminal"/> on session removal to avoid cross-session
    /// leakage.</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, TerminalVerdict> _terminalAt = new();

    private System.Threading.Timer? _pollTimer;
    private bool _disposed;

    /// <summary>
    /// Raised when a tailed subagent's derived state changes. The handler
    /// (SessionManager) updates the <see cref="SubagentInfo"/> on the session
    /// and refreshes the UI. Carries the agent id, new state, active tool
    /// name, and the source (always Transcript once the tailer is driving).
    /// </summary>
    public event EventHandler<SubagentStateEventArgs>? StateChanged;

    /// <summary>Start the polling fallback. Call once at app startup.</summary>
    public void Start()
    {
        if (_pollTimer != null) return;
        _pollTimer = new System.Threading.Timer(PollAll, null, PollIntervalMs, PollIntervalMs);
    }

    /// <summary>
    /// Activate tailing for a subagent's transcript. Idempotent: if already
    /// tailing this exact path, does nothing. Initializes the offset to the
    /// current end of file so only NEW appends are processed (no historical
    /// replay). If the file does not exist yet, the watcher picks it up on
    /// creation.
    /// </summary>
    public void ActivateSubagent(string agentId, string jsonlPath)
    {
        if (string.IsNullOrEmpty(agentId) || string.IsNullOrEmpty(jsonlPath)) return;

        if (_tails.TryGetValue(agentId, out var existing) &&
            string.Equals(existing.Path, jsonlPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (existing != null)
        {
            existing.Dispose();
            _tails.TryRemove(agentId, out _);
        }

        var tail = new SubagentFileTail(agentId, jsonlPath, this);
        _tails[agentId] = tail;
        tail.Start();
    }

    /// <summary>Stop tailing a subagent and discard its offset.</summary>
    public void DeactivateSubagent(string agentId)
    {
        if (_tails.TryRemove(agentId, out var tail))
        {
            tail.Dispose();
        }
        // NOTE: _terminalAt is intentionally NOT cleared here. The terminal
        // memory outlives the tail so the watcher keeps skipping a subagent
        // whose row was removed on a terminal verdict — until the escape hatch
        // (file grew) or session removal clears it.
    }

    /// <summary>
    /// Whether the tailer has derived a terminal state (Completed/Failed) for
    /// this agent. Used by the watcher to skip re-activating a subagent whose
    /// row was already removed on a terminal verdict, preventing the
    /// "disappear → re-spawn (Pending)" flicker.
    /// </summary>
    public bool IsTerminal(string agentId)
        => _terminalAt.ContainsKey(agentId);

    /// <summary>
    /// Whether the terminal verdict for this agent is <b>authoritative</b>
    /// (set by <see cref="MarkAuthoritativeTerminal"/> via a task-notification
    /// or agents_killed signal from the main transcript). Authoritative
    /// verdicts are never recovered by the escape hatch — the subagent is
    /// truly done. Returns false if the agent has no terminal verdict or the
    /// verdict is a silence-derived guess.
    /// </summary>
    public bool IsAuthoritativeTerminal(string agentId)
        => _terminalAt.TryGetValue(agentId, out var v) && v.IsAuthoritative;

    /// <summary>
    /// Try to get the UTC moment the terminal verdict was recorded for this
    /// agent. Returns false if the agent is not in the terminal memory. Used
    /// by the watcher's escape hatch: if the subagent's jsonl last-line
    /// timestamp is later than this moment, the file grew after the verdict →
    /// false terminal → clear and re-activate (only for guess verdicts;
    /// authoritative verdicts are never cleared this way).
    /// </summary>
    public bool TryGetTerminalAt(string agentId, out DateTime terminalAt)
    {
        if (_terminalAt.TryGetValue(agentId, out var v))
        {
            terminalAt = v.At;
            return true;
        }
        terminalAt = default;
        return false;
    }

    /// <summary>
    /// Record an <b>authoritative</b> terminal verdict for a subagent, driven
    /// by a task-notification or agents_killed signal observed in the main
    /// transcript (not by the subagent's own silence). Independent of the tail
    /// lifecycle — must be callable even after the row was removed on a prior
    /// (guess) terminal verdict, because the authoritative signal can arrive
    /// after the row is gone (timing race: guess terminal → row removal →
    /// interrupt signal). An authoritative verdict overrides any prior guess
    /// verdict for the same agent and is never recovered by the escape hatch.
    /// </summary>
    public void MarkAuthoritativeTerminal(string agentId, DateTime atUtc)
    {
        _terminalAt[agentId] = new TerminalVerdict(atUtc, IsAuthoritative: true);
        FileLogger.Info(
            $"subagent authoritative terminal agent={agentId} at={atUtc:o}");
    }

    /// <summary>
    /// Clear the terminal memory for one agent (the escape hatch). Called by
    /// the watcher when it detects the subagent's jsonl grew past a
    /// <b>guess</b> terminal verdict — the subagent was still running, so the
    /// verdict was false and the agent is allowed to be re-activated. Must NOT
    /// be called for authoritative verdicts (those are never false).
    /// </summary>
    public void ClearTerminal(string agentId)
        => _terminalAt.TryRemove(agentId, out _);

    /// <summary>
    /// Clear the terminal memory for a set of agents (session removal). Called
    /// by SessionManager.RemoveSession so terminal verdicts from a previous
    /// session do not leak into a future session that reuses the same agent
    /// ids (agent ids are unique per session in practice, but clearing on
    /// removal is cheap insurance).
    /// </summary>
    public void ClearAllTerminal(IEnumerable<string> agentIds)
    {
        foreach (var id in agentIds)
            _terminalAt.TryRemove(id, out _);
    }

    /// <summary>
    /// Promote any <b>guess</b> terminal verdicts for the given agents to
    /// <b>authoritative</b>, preserving the original verdict moment. Called by
    /// SessionManager.OnSubagentsKilled for every subagent a session has ever
    /// seen: an <c>agents_killed</c> interrupt ends all running subagents, so
    /// any prior silence-derived (guess) verdict for them is confirmed true
    /// and must no longer be recoverable by the escape hatch. Agents with no
    /// verdict, or already-authoritative, are left unchanged.
    /// </summary>
    public void PromoteGuessTerminalsToAuthoritative(IEnumerable<string> agentIds)
    {
        foreach (var id in agentIds)
        {
            if (_terminalAt.TryGetValue(id, out var v) && !v.IsAuthoritative)
            {
                _terminalAt[id] = new TerminalVerdict(v.At, IsAuthoritative: true);
                FileLogger.Info(
                    $"subagent terminal promoted to authoritative agent={id} at={v.At:o}");
            }
        }
    }

    /// <summary>
    /// Snapshot of all currently-tailed subagent agent ids. Used by the
    /// watcher to deactivate tails for subagents that have disappeared.
    /// </summary>
    public IReadOnlyCollection<string> GetActiveAgentIds()
    {
        return _tails.Keys.ToArray();
    }

    /// <summary>
    /// Whether the tailer is currently tailing this agent (i.e. has an active
    /// <see cref="SubagentFileTail"/> for it). Used by the watcher's fallback
    /// stale check to avoid preempting an attached tail: once the tailer owns
    /// an agent, the watcher must not Dispose its tail on the 20s stale
    /// window — the tailer derives the terminal state itself on the 120s
    /// silence threshold.
    /// </summary>
    public bool IsTailing(string agentId)
        => _tails.ContainsKey(agentId);

    private void PollAll(object? state)
    {
        foreach (var kvp in _tails)
        {
            try
            {
                kvp.Value.Poll();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"SubagentTailer poll error for {kvp.Key}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Re-derive the state for a tailed subagent from its accumulated
    /// observations and raise <see cref="StateChanged"/> if it changed. Called
    /// by <see cref="SubagentFileTail"/> after processing new lines, and also
    /// by the periodic poll so <see cref="SubagentState.WaitingApi"/> (a
    /// time-based state) is detected without a new line arriving.
    /// </summary>
    internal void RaiseStateIfChanged(SubagentFileTail tail)
    {
        var derived = tail.DeriveState(DateTime.UtcNow, out var activeToolName);
        if (tail.LastReportedState == derived && tail.LastReportedToolName == activeToolName)
            return;

        tail.LastReportedState = derived;
        tail.LastReportedToolName = activeToolName;

        // Record the terminal verdict so the watcher can skip re-activating
        // this subagent after its row is removed. This silence-derived verdict
        // is a GUESS — the escape hatch (file grew past this moment) clears it
        // if the verdict turns out to be false. An authoritative verdict
        // (task-notification / agents_killed) set via MarkAuthoritativeTerminal
        // overrides this and is never cleared by the escape hatch.
        if (derived == SubagentState.Completed || derived == SubagentState.Failed)
        {
            // Only record a guess if there is no authoritative verdict already
            // present (an authoritative verdict arriving first wins and must
            // not be downgraded to a guess).
            _terminalAt.TryAdd(tail.AgentId,
                new TerminalVerdict(DateTime.UtcNow, IsAuthoritative: false));
        }

        FileLogger.Info(
            $"subagent state {tail.AgentId}: {derived} tool={activeToolName ?? "null"} " +
            $"unpaired={tail.HasUnpairedToolUse} lastUser={tail.LastUserUtc?.ToString("o") ?? "null"} " +
            $"lastAsst={tail.LastAssistantUtc?.ToString("o") ?? "null"}");

        StateChanged?.Invoke(this, new SubagentStateEventArgs
        {
            AgentId = tail.AgentId,
            State = derived,
            ActiveToolName = activeToolName ?? string.Empty,
            Source = StateSource.Transcript,
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer?.Dispose();
        _pollTimer = null;
        foreach (var tail in _tails.Values)
            tail.Dispose();
        _tails.Clear();
    }

    /// <summary>
    /// Per-subagent tail state: the watched path, read offset, FSWatcher, and
    /// the accumulated observations used to derive <see cref="SubagentState"/>.
    /// Guarded by <see cref="_readLock"/> so the FSWatcher thread and the
    /// polling thread do not read concurrently.
    /// </summary>
    internal sealed class SubagentFileTail : IDisposable
    {
        public string Path { get; }
        public string AgentId { get; }
        private readonly SubagentTailer _owner;

        private long _offset;
        private FileSystemWatcher? _watcher;
        private long _lastChangeTicks;
        private readonly object _readLock = new();
        private bool _disposed;

        // Accumulated observations for state derivation (§5.2).
        private readonly HashSet<string> _unpairedToolUseIds = new();
        private string? _lastToolUseName;
        private DateTime? _lastUserUtc;
        private DateTime? _lastAssistantUtc;
        private bool _turnEnded;
        private bool _turnEndedWithError;
        private DateTime _lastEntryUtc = DateTime.UtcNow;

        // The most recent state reported via RaiseStateIfChanged, to suppress
        // redundant events. Tool name is tracked alongside so a tool switch
        // (Bash -> Read) within ToolRunning still raises an update.
        internal SubagentState LastReportedState = (SubagentState)(-1);
        internal string? LastReportedToolName;

        public SubagentFileTail(string agentId, string path, SubagentTailer owner)
        {
            AgentId = agentId;
            Path = path;
            _owner = owner;
        }

        public bool HasUnpairedToolUse
        {
            get
            {
                lock (_readLock)
                {
                    return _unpairedToolUseIds.Count > 0;
                }
            }
        }

        internal DateTime? LastUserUtc
        {
            get { lock (_readLock) { return _lastUserUtc; } }
        }

        internal DateTime? LastAssistantUtc
        {
            get { lock (_readLock) { return _lastAssistantUtc; } }
        }

        public void Start()
        {
            try
            {
                if (File.Exists(Path))
                {
                    using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    _offset = fs.Length;
                }
                else
                {
                    _offset = 0;
                }
            }
            catch
            {
                _offset = 0;
            }

            var dir = System.IO.Path.GetDirectoryName(Path);
            var fileName = System.IO.Path.GetFileName(Path);
            if (dir is null || !Directory.Exists(dir))
            {
                return;
            }

            try
            {
                _watcher = new FileSystemWatcher(dir, fileName)
                {
                    NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite
                                   | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                };
                _watcher.Changed += (_, _) => OnChanged();
                _watcher.Created += (_, _) => OnChanged();
                _watcher.Renamed += (_, e) =>
                {
                    if (string.Equals(e.FullPath, Path, StringComparison.OrdinalIgnoreCase))
                        OnChanged();
                };
                _watcher.Error += (_, e) =>
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"SubagentTailer FSWatcher error for {AgentId}: {e.GetException().Message}");
                };
            }
            catch
            {
                // FSWatcher unavailable — polling fallback still works.
            }

            Poll();
        }

        private void OnChanged()
        {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastChangeTicks);
            if (now - last < WatcherDebounceMs) return;
            Interlocked.Exchange(ref _lastChangeTicks, now);
            ThreadPool.QueueUserWorkItem(_ => Poll());
        }

        public void Poll()
        {
            lock (_readLock)
            {
                if (_disposed) return;
                if (!File.Exists(Path)) return;

                try
                {
                    using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);

                    if (_offset > fs.Length)
                    {
                        // File shrunk (rotation/recreation) — jump to EOF.
                        _offset = fs.Length;
                        _pending.Clear();
                    }

                    fs.Seek(_offset, SeekOrigin.Begin);
                    int newBytes = (int)(fs.Length - fs.Position);
                    if (newBytes <= 0 && _pending.Count == 0)
                    {
                        // No new bytes. Fall through to the post-lock
                        // RaiseStateIfChanged so a time-based state
                        // (WaitingApi) can still fire without a new line.
                    }
                    else
                    {
                        var buf = new byte[_pending.Count + Math.Max(0, newBytes)];
                        if (_pending.Count > 0)
                            _pending.CopyTo(buf, 0);
                        int read = 0;
                        if (newBytes > 0)
                            read = fs.Read(buf, _pending.Count, newBytes);

                        int consumedFromFile = read;
                        int total = _pending.Count + read;

                        _pending.Clear();
                        int lineStart = 0;
                        for (int i = 0; i < total; i++)
                        {
                            if (buf[i] == (byte)'\n')
                            {
                                int lineLen = i - lineStart;
                                if (lineLen > 0 && buf[lineStart + lineLen - 1] == (byte)'\r')
                                    lineLen--;

                                if (lineLen > MaxLineBytes)
                                {
                                    System.Diagnostics.Debug.WriteLine(
                                        $"SubagentTailer: skipping oversized line ({lineLen} bytes) in {Path}");
                                }
                                else
                                {
                                    var lineStr = Encoding.UTF8.GetString(buf, lineStart, lineLen);
                                    ProcessLine(lineStr);
                                }
                                lineStart = i + 1;
                            }
                        }

                        int trailing = total - lineStart;
                        if (trailing > 0)
                        {
                            if (trailing > MaxLineBytes)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"SubagentTailer: dropping oversized partial line ({trailing} bytes) in {Path}");
                            }
                            else
                            {
                                var pendingBytes = new byte[trailing];
                                Array.Copy(buf, lineStart, pendingBytes, 0, trailing);
                                _pending = new List<byte>(pendingBytes);
                            }
                        }

                        _offset += consumedFromFile;
                    }
                }
                catch (IOException)
                {
                    // File locked — next poll retries.
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"SubagentTailer read error for {AgentId}: {ex.Message}");
                }
            }

            // Re-derive state after processing new lines. Done outside the
            // read lock so RaiseStateIfChanged (which fires the event) does
            // not hold the lock while handlers run. Always called — even with
            // no new bytes — so time-based states (WaitingApi) fire on the
            // periodic tick.
            _owner.RaiseStateIfChanged(this);
        }

        // Partial trailing line carried across polls (incomplete write).
        private List<byte> _pending = new();

        private void ProcessLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line[0] != '{') return;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                return;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) return;
                var type = typeProp.GetString();
                if (string.IsNullOrEmpty(type)) return;

                // Timestamp of this entry (UTC). Used for WaitingApi and
                // LastActivityUtc. Falls back to now if absent/malformed.
                DateTime entryUtc = DateTime.UtcNow;
                if (root.TryGetProperty("timestamp", out var tsProp) &&
                    tsProp.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(tsProp.GetString(),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal
                        | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var dt))
                {
                    entryUtc = dt.ToUniversalTime();
                }
                _lastEntryUtc = entryUtc;

                switch (type)
                {
                    case "assistant":
                        ProcessAssistant(root, entryUtc);
                        break;
                    case "user":
                        ProcessUser(root, entryUtc);
                        break;
                    case "system":
                        ProcessSystem(root, entryUtc);
                        break;
                }
            }
        }

        private void ProcessAssistant(JsonElement root, DateTime entryUtc)
        {
            // An assistant entry may carry tool_use blocks (tool starting)
            // and/or text (thinking/reasoning). tool_use ids are added to the
            // unpaired set; the entry timestamp updates LastAssistantUtc.
            _lastAssistantUtc = entryUtc;

            if (!root.TryGetProperty("message", out var msg) ||
                msg.ValueKind != JsonValueKind.Object) return;
            if (!msg.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array) return;

            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("type", out var t)) continue;
                var blockType = t.GetString();
                if (blockType == "tool_use")
                {
                    if (item.TryGetProperty("id", out var idProp))
                    {
                        var id = idProp.GetString();
                        if (!string.IsNullOrEmpty(id))
                        {
                            _unpairedToolUseIds.Add(id);
                        }
                    }
                    if (item.TryGetProperty("name", out var nameProp))
                    {
                        _lastToolUseName = nameProp.GetString();
                    }
                }
            }
        }

        private void ProcessUser(JsonElement root, DateTime entryUtc)
        {
            // A user entry may carry tool_result blocks (tool finished) which
            // pair tool_use ids. A user entry without tool_result is a user
            // message to the subagent — updates LastUserUtc for WaitingApi.
            _lastUserUtc = entryUtc;

            if (!root.TryGetProperty("message", out var msg) ||
                msg.ValueKind != JsonValueKind.Object) return;
            if (!msg.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array) return;

            bool hadToolResult = false;
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("type", out var t)) continue;
                if (t.GetString() != "tool_result") continue;
                hadToolResult = true;
                if (item.TryGetProperty("tool_use_id", out var idProp))
                {
                    var id = idProp.GetString();
                    if (!string.IsNullOrEmpty(id))
                    {
                        _unpairedToolUseIds.Remove(id);
                    }
                }
            }

            // If this user entry had tool_results, it is not a "user message"
            // that should trigger WaitingApi — clear LastUserUtc so the
            // WaitingApi derivation does not fire after a tool completes.
            if (hadToolResult)
            {
                _lastUserUtc = null;
            }
        }

        private void ProcessSystem(JsonElement root, DateTime entryUtc)
        {
            // system entry with subtype "stop_hook_summary" is the turn-end
            // signal. is_error distinguishes Completed vs Failed.
            if (!root.TryGetProperty("subtype", out var subProp)) return;
            if (subProp.GetString() != "stop_hook_summary") return;

            _turnEnded = true;
            _turnEndedWithError = false;
            if (root.TryGetProperty("is_error", out var errProp) &&
                errProp.ValueKind == JsonValueKind.True)
            {
                _turnEndedWithError = true;
            }
            // A turn end clears any unpaired tool_use — the turn is over.
            _unpairedToolUseIds.Clear();
        }

        /// <summary>
        /// Derive the <see cref="SubagentState"/> from the accumulated
        /// observations (§5.2). Called after each poll with new lines AND on
        /// the periodic tick (so WaitingApi fires without a new line).
        /// </summary>
        internal SubagentState DeriveState(DateTime nowUtc, out string? activeToolName)
        {
            lock (_readLock)
            {
                activeToolName = null;

                // Turn-end observed → terminal state.
                if (_turnEnded)
                {
                    return _turnEndedWithError ? SubagentState.Failed : SubagentState.Completed;
                }

                // Unpaired tool_use → tool running.
                if (_unpairedToolUseIds.Count > 0)
                {
                    activeToolName = _lastToolUseName;
                    return SubagentState.ToolRunning;
                }

                // User message with no assistant response for > threshold →
                // waiting for API. This fires when the most recent user entry
                // (that was NOT a tool_result) has no later assistant entry,
                // i.e. the subagent sent a user message and the model has not
                // responded yet.
                if (_lastUserUtc is not null)
                {
                    var noAssistantAfterUser =
                        _lastAssistantUtc is null ||
                        _lastUserUtc.Value > _lastAssistantUtc.Value;
                    if (noAssistantAfterUser &&
                        (nowUtc - _lastUserUtc.Value).TotalSeconds > WaitingApiThresholdSeconds)
                    {
                        return SubagentState.WaitingApi;
                    }
                }

                // Silence-to-terminal: Claude Code does not write a turn-end
                // signal to the subagent transcript, so a subagent that has
                // produced output, has no tool in flight, and has been silent
                // for the threshold is inferred to have finished. This is the
                // authoritative terminal path in practice (the _turnEnded
                // branch above is reserved for a future CC that writes
                // stop_hook_summary to subagent files).
                if (_lastAssistantUtc is not null &&
                    (nowUtc - _lastEntryUtc).TotalSeconds > IdleToCompletedSeconds)
                {
                    return SubagentState.Completed;
                }

                // Otherwise the subagent is thinking (has activity but no
                // unpaired tool and no stale user message).
                if (_lastAssistantUtc is not null || _lastUserUtc is not null)
                {
                    return SubagentState.Thinking;
                }

                // No parseable entries yet — still being spawned.
                return SubagentState.Pending;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _watcher?.Dispose();
            _watcher = null;
        }
    }
}

/// <summary>
/// Event arguments for <see cref="SubagentTailer.StateChanged"/>. Carries the
/// agent id, the newly-derived state, the active tool name (when
/// ToolRunning), and the source (always Transcript from the tailer).
/// </summary>
public class SubagentStateEventArgs : EventArgs
{
    public string AgentId { get; init; } = string.Empty;
    public SubagentState State { get; init; }
    public string ActiveToolName { get; init; } = string.Empty;
    public StateSource Source { get; init; } = StateSource.Transcript;
}

/// <summary>
/// A terminal-state verdict for a subagent: the UTC moment it was recorded and
/// whether it is authoritative. An <b>authoritative</b> verdict is driven by a
/// task-notification or agents_killed signal in the main transcript (the
/// subagent is truly done) and is never recovered by the escape hatch. A
/// <b>guess</b> verdict is derived from subagent-transcript silence
/// (<see cref="SubagentTailer.IdleToCompletedSeconds"/>) and may be recovered
/// by the escape hatch when the jsonl grows past <see cref="At"/>.
/// </summary>
public readonly record struct TerminalVerdict(DateTime At, bool IsAuthoritative);
