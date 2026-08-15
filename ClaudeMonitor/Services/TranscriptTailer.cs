using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClaudeMonitor.Models;

namespace ClaudeMonitor.Services;

/// <summary>
/// Tails each active session's main transcript JSONL file
/// (<c>~/.claude/projects/&lt;encoded&gt;/&lt;sessionId&gt;.jsonl</c>) and
/// reports authoritatively-observed main-agent activity to the
/// <see cref="SessionManager"/> (TASKS.md §2/§3).
///
/// The transcript is the source of truth: it records every tool_use (in
/// assistant entries) and tool_result (in user entries), plus the turn-end
/// signal (system entry with subtype "stop_hook_summary"). Hooks are
/// low-latency triggers but can be lost or reordered; the transcript corrects
/// the in-memory state once it lands on disk.
///
/// Each file is tailed incrementally from a per-file offset: on activation
/// the offset is set to the current end of file so only NEW lines are read
/// (no historical replay). A <see cref="FileSystemWatcher"/> catches appends
/// in real time, with a 500ms polling fallback (required on Windows where
/// FSWatcher can miss events on append-heavy files). Files are opened with
/// <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/> so
/// Claude Code's write lock does not block reads.
/// </summary>
public class TranscriptTailer : IDisposable
{
    /// <summary>
    /// Polling interval for the append-detection fallback. FSWatcher on
    /// Windows can miss events on files held open for append, so a 500ms
    /// poll (TASKS.md §2.2) guarantees we see new lines within that window.
    /// </summary>
    private const int PollIntervalMs = 500;

    /// <summary>
    /// Debounce for FSWatcher events: multiple change notifications for the
    /// same file within this window are coalesced into one read (§7).
    /// </summary>
    private const int WatcherDebounceMs = 100;

    /// <summary>
    /// A single JSONL line larger than this is skipped with a warning (§7):
    /// assistant messages with huge attachments can be tens of KB, but a
    /// line over 1MB is almost certainly a malformed/partial write.
    /// </summary>
    private const long MaxLineBytes = 1024 * 1024;

    private readonly SessionManager _sessionManager;
    private readonly ConcurrentDictionary<string, FileTail> _tails = new();
    private System.Threading.Timer? _pollTimer;
    private bool _disposed;

    public TranscriptTailer(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    /// <summary>Start the polling fallback. Call once at app startup.</summary>
    public void Start()
    {
        if (_pollTimer != null) return;
        _pollTimer = new System.Threading.Timer(PollAll, null, PollIntervalMs, PollIntervalMs);
    }

    /// <summary>
    /// Activate tailing for a session's main transcript. Resolves the path
    /// from the project path + session id, initializes the offset to the
    /// current end of file (so only new lines are read), and starts a
    /// <see cref="FileSystemWatcher"/> on the parent directory. Safe to call
    /// again for an already-active session (idempotent). If the transcript
    /// file does not exist yet, the watcher will pick it up when created.
    /// </summary>
    public void ActivateFile(string sessionId, string projectPath)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(projectPath)) return;

        var transcriptPath = ClaudePaths.ResolveTranscriptPath(projectPath, sessionId);
        if (transcriptPath is null) return;

        FileLogger.Info($"tailer activate {sessionId}: {transcriptPath} exists={File.Exists(transcriptPath)}");

        // Idempotent: if already tailing this exact path, nothing to do.
        if (_tails.TryGetValue(sessionId, out var existing) &&
            string.Equals(existing.Path, transcriptPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Drop any previous tail for this session (path may have changed).
        if (existing != null)
        {
            existing.Dispose();
            _tails.TryRemove(sessionId, out _);
        }

        var tail = new FileTail(sessionId, transcriptPath, _sessionManager);
        _tails[sessionId] = tail;
        tail.Start();
    }

    /// <summary>Stop tailing a session's transcript and discard its offset.</summary>
    public void DeactivateFile(string sessionId)
    {
        if (_tails.TryRemove(sessionId, out var tail))
        {
            tail.Dispose();
        }
    }

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
                    $"TranscriptTailer poll error for {kvp.Key}: {ex.Message}");
            }
        }
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
    /// Per-file tail state: the watched path, the current read offset, and
    /// the FSWatcher. Owns parsing new lines into <see cref="SessionManager"/>
    /// calls. Guarded by <see cref="_readLock"/> so the FSWatcher thread and
    /// the polling thread do not read concurrently.
    /// </summary>
    private sealed class FileTail : IDisposable
    {
        public string Path { get; }
        private readonly string _sessionId;
        private readonly SessionManager _sessionManager;
        private long _offset;
        private FileSystemWatcher? _watcher;
        private long _lastChangeTicks; // Environment.TickCount64 at last FSWatcher fire
        private readonly object _readLock = new();
        private bool _disposed;

        /// <summary>
        /// Bytes from the end of the last read that did NOT end in a newline —
        /// an incomplete line mid-write. Carried across polls and prepended to
        /// the next read so the line is assembled once its trailing bytes
        /// (including the newline) land on disk. Without this, a partial line
        /// at EOF would be processed as a (malformed) complete line and the
        /// offset advanced past it, losing its first half on the next poll.
        /// </summary>
        private List<byte> _pending = new();

        public FileTail(string sessionId, string path, SessionManager sessionManager)
        {
            _sessionId = sessionId;
            Path = path;
            _sessionManager = sessionManager;
        }

        /// <summary>
        /// Begin tailing. Initializes the offset to the current end of file
        /// (so only NEW appends are processed — no historical replay) and
        /// starts the FSWatcher on the parent directory.
        /// </summary>
        public void Start()
        {
            // Initialize offset to EOF so we only read lines appended AFTER
            // activation. This avoids replaying the entire session history on
            // every CC-Pulse launch (§7).
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

            // Watch the parent directory for changes to this file. Watching
            // the directory (rather than the file) survives the file being
            // renamed/recreated and catches the initial creation when the
            // transcript does not yet exist.
            var dir = System.IO.Path.GetDirectoryName(Path);
            var fileName = System.IO.Path.GetFileName(Path);
            if (dir is null || !Directory.Exists(dir))
            {
                // Directory may not exist yet (no sessions for this project).
                // The polling fallback will still attempt direct reads, and a
                // later ActivateFile (after the dir is created) can re-arm.
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
                        $"TranscriptTailer FSWatcher error for {_sessionId}: {e.GetException().Message}");
                };
            }
            catch
            {
                // FSWatcher unavailable — polling fallback still works.
            }

            // Read any lines that landed between offset init and watcher arm.
            Poll();
        }

        private void OnChanged()
        {
            // Debounce: coalesce rapid notifications into one read.
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastChangeTicks);
            if (now - last < WatcherDebounceMs) return;
            Interlocked.Exchange(ref _lastChangeTicks, now);
            ThreadPool.QueueUserWorkItem(_ => Poll());
        }

        /// <summary>
        /// Read any new bytes beyond <see cref="_offset"/> and parse complete
        /// lines. Partial trailing bytes (an incomplete line mid-write) are
        /// left for the next poll. Safe to call from the poll timer or the
        /// FSWatcher thread; <see cref="_readLock"/> serializes reads.
        /// </summary>
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

                    // The file may have been truncated/rotated (compact can
                    // start a new file). If the offset is past EOF, the file
                    // was recreated smaller. Rather than replay from 0 (which
                    // would re-process historical tool_use/tool_result lines
                    // and corrupt the in-memory unpaired-tool set), jump to
                    // EOF and only read NEW appends. Compaction state is
                    // cleared upstream by SessionManager.MarkCompacting; the
                    // tailer just needs to avoid replaying history.
                    if (_offset > fs.Length)
                    {
                        FileLogger.Warn($"tailer {Path}: file shrunk (offset={_offset} > len={fs.Length}), jumping to EOF (rotation/compact)");
                        _offset = fs.Length;
                        _pending.Clear();
                    }

                    // Read all NEW bytes since the last poll into a buffer,
                    // prepending any partial line carried over from last time.
                    fs.Seek(_offset, SeekOrigin.Begin);
                    int newBytes = (int)(fs.Length - fs.Position);
                    if (newBytes <= 0 && _pending.Count == 0) return;

                    var buf = new byte[_pending.Count + Math.Max(0, newBytes)];
                    if (_pending.Count > 0)
                        _pending.CopyTo(buf, 0);
                    int read = 0;
                    if (newBytes > 0)
                        read = fs.Read(buf, _pending.Count, newBytes);

                    // Bytes consumed from the file this poll. The offset
                    // ALWAYS advances by exactly the bytes read from disk
                    // this poll. A trailing partial line (no \n) is held in
                    // _pending and prepended to next poll's buffer; it is NOT
                    // re-read from the file. Rewinding the offset here would
                    // duplicate those bytes (once via _pending prepend, once
                    // via the re-read), corrupting the assembled line and
                    // causing JsonDocument.Parse to reject it — silently
                    // dropping the transcript event.
                    int consumedFromFile = read;
                    int total = _pending.Count + read;

                    // Split into lines on \n. A trailing chunk with no \n is a
                    // partial line: keep it in _pending for next poll.
                    _pending.Clear();
                    int lineStart = 0;
                    for (int i = 0; i < total; i++)
                    {
                        if (buf[i] == (byte)'\n')
                        {
                            int lineLen = i - lineStart;
                            // Strip a trailing \r (CRLF) if present.
                            if (lineLen > 0 && buf[lineStart + lineLen - 1] == (byte)'\r')
                                lineLen--;

                            // Skip oversized lines (§7).
                            if (lineLen > MaxLineBytes)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"TranscriptTailer: skipping oversized line ({lineLen} bytes) in {Path}");
                            }
                            else
                            {
                                var lineStr = Encoding.UTF8.GetString(buf, lineStart, lineLen);
                                ProcessLine(lineStr);
                            }
                            lineStart = i + 1;
                        }
                    }

                    // Trailing bytes without a \n are an incomplete line. Hold
                    // them in _pending; they are prepended to next poll's
                    // buffer (NOT re-read from the file — the offset already
                    // advanced past them). When the rest of the line lands on
                    // disk, next poll assembles the full line from
                    // [_pending][new bytes].
                    int trailing = total - lineStart;
                    if (trailing > 0)
                    {
                        // If the partial line is already oversized, drop it to
                        // avoid unbounded _pending growth (a malformed blob
                        // with no newline).
                        if (trailing > MaxLineBytes)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"TranscriptTailer: dropping oversized partial line ({trailing} bytes) in {Path}");
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
                catch (IOException)
                {
                    // File locked or transiently unavailable — next poll retries.
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"TranscriptTailer read error for {_sessionId}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Parse one JSONL line and dispatch main-agent-relevant entries to
        /// the <see cref="SessionManager"/>. Malformed lines are logged and
        /// skipped (§3.3) — never throw, so a single bad line cannot break
        /// the tail.
        /// </summary>
        private void ProcessLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line[0] != '{') return; // quick reject non-JSON

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // Malformed line — skip but do not interrupt the tail.
                return;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) return;
                var type = typeProp.GetString();
                if (string.IsNullOrEmpty(type)) return;

                // Ignore subagent (sidechain) entries — this tailer tracks the
                // MAIN agent only. Subagent state is handled by SubagentWatcher.
                if (root.TryGetProperty("isSidechain", out var scProp) &&
                    scProp.ValueKind == JsonValueKind.True)
                {
                    return;
                }

                switch (type)
                {
                    case "assistant":
                        ProcessAssistant(root);
                        break;
                    case "user":
                        ProcessUser(root);
                        break;
                    case "system":
                        ProcessSystem(root);
                        break;
                }
            }
        }

        /// <summary>
        /// An assistant entry may carry one or more tool_use blocks in
        /// message.content[]. Each tool_use id is reported as an in-flight
        /// tool (authoritative Busy signal). The entry timestamp is also
        /// reported so the reconciler can derive the Thinking state (assistant
        /// activity observed) and detect WaitingApi (user prompt with no
        /// assistant response).
        /// </summary>
        private void ProcessAssistant(JsonElement root)
        {
            var atUtc = ExtractTimestamp(root);
            _sessionManager.OnTranscriptAssistantMessage(_sessionId, atUtc);

            if (!root.TryGetProperty("message", out var msg) ||
                msg.ValueKind != JsonValueKind.Object) return;
            if (!msg.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array) return;

            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("type", out var t) || t.GetString() != "tool_use") continue;
                if (!item.TryGetProperty("id", out var idProp)) continue;
                var id = idProp.GetString();
                if (string.IsNullOrEmpty(id)) continue;
                // The tool_use block carries the tool name (e.g. "Bash", "Read")
                // in its "name" field — reported alongside the id so the
                // fine-grained state can show "running: Bash".
                var toolName = item.TryGetProperty("name", out var nameProp)
                    ? nameProp.GetString() ?? string.Empty
                    : string.Empty;
                _sessionManager.OnTranscriptToolUse(_sessionId, id, toolName);
            }
        }

        /// <summary>
        /// A user entry may carry tool_result blocks in message.content[],
        /// each pairing a tool_use_id. Reporting it clears the in-flight tool.
        /// A user entry WITHOUT any tool_result is a real user message (a new
        /// prompt); its timestamp is reported so the reconciler can detect
        /// WaitingApi (user prompt with no assistant response for &gt; 10s).
        /// A tool_result-bearing entry is NOT a real user message and does not
        /// start a WaitingApi window (it is the tool finishing, not the user
        /// speaking).
        ///
        /// <para>Note: a real user prompt's <c>message.content</c> is a
        /// <b>string</b> (e.g. <c>"run the tests"</c>), not an array — only
        /// tool_result-bearing user entries use the array form. The timestamp
        /// is extracted up front (it lives on the top-level entry, not in
        /// <c>message</c>) so the WaitingApi window starts regardless of the
        /// content shape.</para>
        /// </summary>
        private void ProcessUser(JsonElement root)
        {
            // The timestamp lives on the top-level entry, available regardless
            // of the message.content shape (string vs array).
            var atUtc = ExtractTimestamp(root);

            if (!root.TryGetProperty("message", out var msg) ||
                msg.ValueKind != JsonValueKind.Object)
            {
                // No message object — treat as a real user message.
                _sessionManager.OnTranscriptUserMessage(_sessionId, atUtc);
                return;
            }

            bool hadToolResult = false;
            if (msg.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.Array)
            {
                // Array content: pair any tool_result blocks.
                foreach (var item in content.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("type", out var t) || t.GetString() != "tool_result") continue;
                    hadToolResult = true;
                    if (!item.TryGetProperty("tool_use_id", out var idProp)) continue;
                    var id = idProp.GetString();
                    if (string.IsNullOrEmpty(id)) continue;
                    _sessionManager.OnTranscriptToolResult(_sessionId, id);
                }
            }
            // String/other content: hadToolResult stays false → real user message.

            // A real user message (no tool_result) starts a WaitingApi window:
            // if no assistant response follows within the threshold, the agent
            // is waiting for the API. A tool_result entry is the tool
            // finishing, not the user speaking — do not start the window.
            if (!hadToolResult)
            {
                _sessionManager.OnTranscriptUserMessage(_sessionId, atUtc);
            }
        }

        /// <summary>
        /// Extract the UTC timestamp from a transcript entry's top-level
        /// <c>timestamp</c> field. Falls back to <see cref="DateTime.UtcNow"/>
        /// if absent or malformed (same policy as <see cref="ProcessSystem"/>).
        /// </summary>
        private static DateTime ExtractTimestamp(JsonElement root)
        {
            if (root.TryGetProperty("timestamp", out var tsProp) &&
                tsProp.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(tsProp.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                    | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var dt))
            {
                return dt.ToUniversalTime();
            }
            return DateTime.UtcNow;
        }

        /// <summary>
        /// A system entry with subtype "stop_hook_summary" is the
        /// authoritative turn-end signal (current Claude Code versions no
        /// longer write a top-level "result" entry). Reporting it lets the
        /// reconciler set Idle authoritatively.
        /// </summary>
        private void ProcessSystem(JsonElement root)
        {
            if (!root.TryGetProperty("subtype", out var subProp)) return;
            if (subProp.GetString() != "stop_hook_summary") return;

            _sessionManager.OnTranscriptTurnEnd(_sessionId, ExtractTimestamp(root));
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
