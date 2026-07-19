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
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2),
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
            };
            if (!string.IsNullOrEmpty(projectPath))
                payload["projectPath"] = projectPath;

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // POST synchronously — we MUST wait for the response before exiting.
            // Fire-and-forget (_ = PostAsync) causes the process to exit before
            // the HTTP request is sent, since the runtime doesn't wait for
            // orphaned Tasks when the process terminates.
            _ = _httpClient.PostAsync($"{HookServerUrl}/{endpoint}", content).Result;

            return 0;
        }
        catch (Exception)
        {
            // Silently fail — hooks must not block Claude Code
            return 1;
        }
    }
}
