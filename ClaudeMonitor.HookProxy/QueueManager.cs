using System;
using System.IO;
using System.Text;

namespace ClaudeMonitor.HookProxy;

/// <summary>
/// Best-effort local NDJSON queue for hook events that could not be delivered
/// to the HookServer (server down, timeout, connection refused). Per
/// TASKS.md §5, hooks must never block Claude Code: when the POST fails, the
/// serialized payload is appended here instead, and CC-Pulse replays the
/// queue on next launch.
///
/// The queue is a single file at ~/.claude/cc-pulse-queue.ndjson, one JSON
/// object per line. Writes are append-only and best-effort (any IO error is
/// swallowed). Replay is performed by the main CC-Pulse app at startup
/// (see App.OnStartup), which reads the file, POSTs each line to the
/// HookServer, and truncates the file on success.
///
/// This class is dependency-free so it can live in the HookProxy project
/// (which cannot reference the WPF-bearing ClaudeMonitor project).
/// </summary>
internal static class QueueManager
{
    private static readonly string QueuePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "cc-pulse-queue.ndjson");

    /// <summary>Path to the queue file (used by the replayer in the main app).</summary>
    public static string QueueFilePath => QueuePath;

    /// <summary>
    /// Append a serialized hook payload (one JSON object) to the queue file.
    /// Best-effort: any IO error is swallowed so the hook process never blocks.
    /// </summary>
    public static void Enqueue(string jsonPayload)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(QueuePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            // Append a single line terminated by '\n'. FileShare.ReadWrite lets
            // the replayer read concurrently without locking.
            File.AppendAllText(QueuePath, jsonPayload + "\n", Encoding.UTF8);
        }
        catch
        {
            // Swallow — queueing is best-effort; never block Claude Code.
        }
    }
}
