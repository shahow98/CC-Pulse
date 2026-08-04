using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;

namespace ClaudeMonitor.Services;

/// <summary>
/// Best-effort local NDJSON queue for hook events that could not be delivered
/// to the HookServer (server down, timeout, connection refused). Per
/// TASKS.md §5, hooks must never block Claude Code: when the POST fails, the
/// serialized payload is appended to <c>~/.claude/cc-pulse-queue.ndjson</c>
/// (one JSON object per line), and CC-Pulse replays the queue on next launch
/// via <see cref="Replay"/>.
///
/// This is the main-app counterpart to the dependency-free writer in the
/// HookProxy project. Both write to the same file path; this class adds the
/// replay (read + re-POST + truncate) logic that the hook process cannot do.
/// </summary>
public static class QueueManager
{
    private static readonly string QueuePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "cc-pulse-queue.ndjson");

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(1),
    };

    private const string HookServerUrl = "http://localhost:8765";

    /// <summary>Append a serialized hook payload (one JSON object) to the queue file.</summary>
    public static void Enqueue(string jsonPayload)
    {
        try
        {
            var dir = Path.GetDirectoryName(QueuePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(QueuePath, jsonPayload + "\n", Encoding.UTF8);
        }
        catch
        {
            // Swallow — queueing is best-effort; never block Claude Code.
        }
    }

    /// <summary>
    /// Replay queued hook events to the HookServer. Reads each NDJSON line,
    /// POSTs it to the endpoint encoded in the payload's "endpoint" field
    /// (falling back to "idle"), and truncates the queue file on success.
    /// Lines that fail to send are retained for the next replay. Called once
    /// at app startup before the HookServer begins serving new events.
    /// </summary>
    public static void Replay()
    {
        if (!File.Exists(QueuePath)) return;

        List<string> lines;
        try
        {
            lines = new List<string>(File.ReadAllLines(QueuePath));
        }
        catch
        {
            return;
        }

        if (lines.Count == 0) return;

        var failed = new List<string>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!TryExtractEndpoint(line, out var endpoint))
                endpoint = "idle";

            try
            {
                var content = new StringContent(line, Encoding.UTF8, "application/json");
                _ = _httpClient.PostAsync($"{HookServerUrl}/{endpoint}", content).Result;
            }
            catch
            {
                // Server still down — keep this line for the next replay.
                failed.Add(line);
            }
        }

        // Rewrite the queue with only the lines that still failed. If all
        // succeeded, truncate to empty (then delete the empty file).
        try
        {
            if (failed.Count == 0)
            {
                if (File.Exists(QueuePath)) File.Delete(QueuePath);
            }
            else
            {
                File.WriteAllText(QueuePath, string.Join("\n", failed) + "\n", Encoding.UTF8);
            }
        }
        catch
        {
            // Best-effort — a stale queue file is harmless.
        }
    }

    /// <summary>
    /// Extract the "endpoint" field from a queued JSON payload without a full
    /// DOM parse (the payload is flat key/value strings). Returns false if not
    /// found.
    /// </summary>
    private static bool TryExtractEndpoint(string json, out string endpoint)
    {
        endpoint = "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("endpoint", out var ep))
            {
                endpoint = ep.GetString() ?? "";
                return endpoint.Length > 0;
            }
        }
        catch { /* ignore */ }
        return false;
    }
}
