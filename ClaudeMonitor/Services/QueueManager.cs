using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ClaudeMonitor.Services;

/// <summary>
/// Best-effort local NDJSON queue for hook events that could not be delivered
/// to the HookServer (server down, timeout, connection refused). Per
/// TASKS.md §5, hooks must never block Claude Code: when the POST fails, the
/// serialized payload is appended to <c>~/.claude/cc-pulse-queue.ndjson</c>
/// (one JSON object per line).
///
/// This is the main-app counterpart to the dependency-free writer in the
/// HookProxy project. Both write to the same file path.
///
/// <para>
/// <b>Replay is intentionally NOT performed on launch.</b> The queue holds
/// events from while CC-Pulse was offline — by the time CC-Pulse restarts,
/// those sessions may have ended, and re-delivering stale PreToolUse /
/// PostToolUse events resurrects ghost sessions stuck in Busy (a trailing
/// PreToolUse with no matching PostToolUse leaves <c>activeOps</c> true, so
/// the watchdog's 30-min long-timeout tier keeps the phantom Busy for half
/// an hour). CC-Pulse instead starts from the <i>current</i> real state:
/// <see cref="Discard"/> drops the stale queue, and live SessionStart hooks
/// build fresh sessions going forward. The queue file is retained as a
/// diagnostic artifact of what Claude Code did while CC-Pulse was down.
/// </para>
/// </summary>
public static class QueueManager
{
    private static readonly string QueuePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "cc-pulse-queue.ndjson");

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
    /// Drop the stale hook queue. Called once at app startup. The queue holds
    /// events queued while CC-Pulse was offline; re-delivering them would
    /// resurrect ended sessions (a trailing PreToolUse with no PostToolUse
    /// leaves a ghost Busy). CC-Pulse starts from the current real state
    /// instead — live SessionStart hooks build fresh sessions. The file is
    /// deleted (not replayed); its contents are already on disk as a
    /// diagnostic record of offline activity.
    /// </summary>
    public static void Discard()
    {
        try
        {
            if (File.Exists(QueuePath)) File.Delete(QueuePath);
        }
        catch
        {
            // Best-effort — a stale queue file is harmless (it will not be
            // replayed, and the next Enqueue simply appends to it).
        }
    }
}
