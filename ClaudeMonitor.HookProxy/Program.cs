using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ClaudeMonitor.HookProxy;

/// <summary>
/// Lightweight console-mode hook proxy for CC-Pulse.
///
/// Claude Code hooks pass session context via stdin JSON, but GUI subsystem
/// executables (WinExe) may not properly inherit stdin pipes on Windows.
/// This console app (Exe subsystem) reliably reads stdin and forwards
/// the session info to the CC-Pulse HookServer via HTTP.
///
/// Usage: CC-Pulse-Hook.exe &lt;endpoint&gt;
///   endpoint = start | busy | idle | interactive | end
///
/// Claude Code passes JSON on stdin with fields like:
///   session_id, cwd, hook_event_name, source/reason, etc.
/// </summary>
internal static class Program
{
    private const string HookServerUrl = "http://localhost:8765";
    // 1s timeout per TASKS.md §5 (≤1s). The POST is synchronous because the
    // process must outlive the request (fire-and-forget loses the event when
    // the runtime tears down orphaned Tasks on exit). The HookServer handles
    // requests in <10ms, so 1s is ample and bounds the worst-case block.
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(1),
    };

    static int Main(string[] args)
    {
        var endpoint = args.Length > 0 ? args[0] : "idle";

        try
        {
            // Read JSON from stdin (Claude Code passes hook context via stdin)
            string input = "";
            if (Console.IsInputRedirected)
            {
                input = Console.In.ReadToEnd();
            }

            var sessionId = "";
            var projectPath = "";
            var toolName = "";
            // Standardized metadata per TASKS.md §3.4: carry the originating
            // hook event name, tool_use_id (for Pre/PostToolUse pairing and
            // idempotency), source (SessionStart: startup/resume/clear/compact),
            // and Notification fields (message/title/type) so the HookServer
            // can route precisely instead of guessing from toolName.
            var hookEvent = "";
            var toolUseId = "";
            var source = "";
            var message = "";
            var title = "";
            var notifType = "";

            if (!string.IsNullOrEmpty(input))
            {
                try
                {
                    using var doc = JsonDocument.Parse(input);
                    if (doc.RootElement.TryGetProperty("session_id", out var sidProp))
                        sessionId = sidProp.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("cwd", out var cwdProp))
                        projectPath = cwdProp.GetString() ?? "";
                    // tool_name is present on PreToolUse/PostToolUse hooks.
                    // When it is "Agent", the main agent is launching a subagent.
                    if (doc.RootElement.TryGetProperty("tool_name", out var tnProp))
                        toolName = tnProp.GetString() ?? "";
                    // hook_event_name is present on every hook payload (e.g.
                    // "UserPromptSubmit", "PreToolUse", "SessionStart").
                    if (doc.RootElement.TryGetProperty("hook_event_name", out var heProp))
                        hookEvent = heProp.GetString() ?? "";
                    // tool_use_id pairs PreToolUse with PostToolUse for the
                    // same invocation, enabling ActiveTools tracking + idempotency.
                    if (doc.RootElement.TryGetProperty("tool_use_id", out var tuiProp))
                        toolUseId = tuiProp.GetString() ?? "";
                    // source disambiguates SessionStart (startup/resume/clear/compact).
                    if (doc.RootElement.TryGetProperty("source", out var srcProp))
                        source = srcProp.GetString() ?? "";
                    // Notification payload fields, retained for classification.
                    if (doc.RootElement.TryGetProperty("message", out var msgProp))
                        message = msgProp.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("title", out var titleProp))
                        title = titleProp.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("type", out var typeProp))
                        notifType = typeProp.GetString() ?? "";
                }
                catch (JsonException)
                {
                    // If not valid JSON, fall through to env vars
                }
            }

            // Fallback to environment variables
            // Note: CLAUDE_SESSION_ID may not be set in hook processes (only in stdin JSON).
            // CLAUDE_CODE_SESSION_ID is available in Bash tool subprocesses (v2.1.132+).
            sessionId = string.IsNullOrEmpty(sessionId)
                ? Environment.GetEnvironmentVariable("CLAUDE_SESSION_ID")
                  ?? Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID")
                  ?? "unknown"
                : sessionId;
            projectPath = string.IsNullOrEmpty(projectPath)
                ? Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") ?? ""
                : projectPath;

            // Build JSON payload
            var payload = new Dictionary<string, string>
            {
                ["sessionId"] = sessionId,
                ["endpoint"] = endpoint,
            };
            if (!string.IsNullOrEmpty(projectPath))
                payload["projectPath"] = projectPath;
            if (!string.IsNullOrEmpty(toolName))
                payload["toolName"] = toolName;
            if (!string.IsNullOrEmpty(hookEvent))
                payload["hookEvent"] = hookEvent;
            if (!string.IsNullOrEmpty(toolUseId))
                payload["toolUseId"] = toolUseId;
            if (!string.IsNullOrEmpty(source))
                payload["source"] = source;
            if (!string.IsNullOrEmpty(message))
                payload["message"] = message;
            if (!string.IsNullOrEmpty(title))
                payload["title"] = title;
            if (!string.IsNullOrEmpty(notifType))
                payload["type"] = notifType;

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // POST synchronously — we MUST wait for the response before exiting.
            // Fire-and-forget (_ = PostAsync) causes the process to exit before
            // the HTTP request is sent, since the runtime doesn't wait for
            // orphaned Tasks when the process terminates.
            try
            {
                _ = _httpClient.PostAsync($"{HookServerUrl}/{endpoint}", content).Result;
                return 0;
            }
            catch (Exception)
            {
                // HookServer unreachable — enqueue to local NDJSON queue so the
                // event is replayed on next CC-Pulse launch (TASKS.md §5).
                // Never block Claude Code: queue write is best-effort.
                try { QueueManager.Enqueue(json); } catch { /* ignore */ }
                return 1;
            }
        }
        catch (Exception)
        {
            // Silently fail — hooks must not block Claude Code
            return 1;
        }
    }
}
