using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ClaudeMonitor.Services;

/// <summary>
/// Sends hook status updates to the CC-Pulse HookServer via HTTP.
/// Replaces the curl-based cc-pulse-hook.cmd / cc-pulse-hook.sh scripts.
/// </summary>
public static class HookRunner
{
    private const string HookServerUrl = "http://localhost:8765";
    // 1s timeout per TASKS.md §5 (≤1s). Synchronous POST is required so the
    // event is actually sent before the process exits.
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(1),
    };

    /// <summary>
    /// Run the hook: read stdin JSON, extract session info, POST to HookServer.
    /// Returns immediately (fire-and-forget) to avoid blocking Claude Code.
    /// </summary>
    public static int Run(string endpoint)
    {
        if (string.IsNullOrEmpty(endpoint))
            endpoint = "idle";

        try
        {
            // Read JSON from stdin (Claude Code passes hook context via stdin)
            var input = Console.IsInputRedirected ? Console.In.ReadToEnd() : "";

            var sessionId = "";
            var projectPath = "";
            var toolName = "";
            var agentId = "";
            // Standardized metadata per TASKS.md §3.4 (mirrors HookProxy).
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
                    if (doc.RootElement.TryGetProperty("tool_name", out var tnProp))
                        toolName = tnProp.GetString() ?? "";
                    // SubagentStop/SubagentStart payloads carry agent_id (the
                    // subagent's identifier, matching its agent-<id>.jsonl
                    // filename). Used to remove the exact subagent row on stop.
                    if (doc.RootElement.TryGetProperty("agent_id", out var aidProp))
                        agentId = aidProp.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("hook_event_name", out var heProp))
                        hookEvent = heProp.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("tool_use_id", out var tuiProp))
                        toolUseId = tuiProp.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("source", out var srcProp))
                        source = srcProp.GetString() ?? "";
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
            if (!string.IsNullOrEmpty(agentId))
                payload["agentId"] = agentId;
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
