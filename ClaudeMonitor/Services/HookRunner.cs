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
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2),
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

            if (!string.IsNullOrEmpty(input))
            {
                try
                {
                    using var doc = JsonDocument.Parse(input);
                    if (doc.RootElement.TryGetProperty("session_id", out var sidProp))
                        sessionId = sidProp.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("cwd", out var cwdProp))
                        projectPath = cwdProp.GetString() ?? "";
                }
                catch (JsonException)
                {
                    // If not valid JSON, fall through to env vars
                }
            }

            // Fallback to environment variables
            sessionId = string.IsNullOrEmpty(sessionId)
                ? Environment.GetEnvironmentVariable("CLAUDE_SESSION_ID") ?? "unknown"
                : sessionId;
            projectPath = string.IsNullOrEmpty(projectPath)
                ? Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") ?? ""
                : projectPath;

            // Build JSON payload
            var payload = new Dictionary<string, string>
            {
                ["sessionId"] = sessionId,
            };
            if (!string.IsNullOrEmpty(projectPath))
                payload["projectPath"] = projectPath;

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Fire-and-forget POST (don't wait for response to avoid blocking Claude Code)
            _ = _httpClient.PostAsync($"{HookServerUrl}/{endpoint}", content);

            return 0;
        }
        catch (Exception)
        {
            // Silently fail — hooks must not block Claude Code
            return 1;
        }
    }
}
