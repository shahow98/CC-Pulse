using System;

namespace ClaudeMonitor.Models;

/// <summary>
/// A state-machine anomaly detected by the Transcript/Hook reconciler
/// (TASKS.md §3.6 / §4.3). Recorded on the session for debugging and
/// accuracy analysis. Examples: a Stop received while tools are still in
/// flight, a hook event the transcript never confirmed, or a conflict
/// between hook and transcript state.
/// </summary>
public record AnomalyRecord(string Type, DateTime AtUtc, string Detail)
{
    /// <summary>The kind of anomaly (see remarks for known values).</summary>
    /// <remarks>
    /// Known types:
    /// <list type="bullet">
    /// <item><c>stop_with_unpaired_tool_use</c> — turn ended (transcript
    /// stop_hook_summary) but tool_use ids had no matching tool_result.</item>
    /// <item><c>hook_missed</c> — transcript observed a tool_use that no
    /// PreToolUse hook announced.</item>
    /// <item><c>grace_expired</c> — a hook-driven Busy state was not
    /// confirmed by the transcript within the grace period.</item>
    /// <item><c>transcript_hook_conflict</c> — hook and transcript disagree
    /// on Busy/Idle outside the grace window.</item>
    /// </list>
    /// </remarks>
    public string Type { get; init; } = Type;

    /// <summary>When the anomaly was detected (UTC).</summary>
    public DateTime AtUtc { get; init; } = AtUtc;

    /// <summary>Human-readable detail for diagnosis.</summary>
    public string Detail { get; init; } = Detail;
}

/// <summary>
/// The source that determined a session's main-agent status. Used by the
/// reconciler to report confidence (TASKS.md §6 <c>source</c> field).
/// </summary>
public enum StateSource
{
    /// <summary>Status derived purely from hook events (low latency, lower confidence).</summary>
    Hook,

    /// <summary>Status derived purely from transcript entries (authoritative).</summary>
    Transcript,

    /// <summary>Status derived by reconciling hook + transcript (grace period, etc.).</summary>
    Reconciled,
}
