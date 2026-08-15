using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeMonitor.Models;

namespace ClaudeMonitor.Services;

/// <summary>
/// HTTP listener that receives status updates from Claude Code hooks.
/// Listens on http://localhost:8765/ and routes POST requests to SessionManager.
/// </summary>
public class HookServer : IDisposable
{
    private const string Prefix = "http://localhost:8765/";
    private readonly HttpListener _listener;
    private readonly SessionManager _sessionManager;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public HookServer(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
        _listener = new HttpListener();
        _listener.Prefixes.Add(Prefix);
    }

    /// <summary>Start listening for HTTP requests.</summary>
    public void Start()
    {
        if (_cts != null) return; // Already running

        _cts = new CancellationTokenSource();
        _listener.Start();
        _ = ListenAsync(_cts.Token);
    }

    /// <summary>Stop listening and release resources.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = null;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context, ct), ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            // GET /sessions — diagnostic endpoint to list active sessions
            if (request.HttpMethod == "GET" && (request.Url?.AbsolutePath.Trim('/').Equals("sessions", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                var sessions = _sessionManager.GetAllSessions();
                var sessionList = string.Join("\n", sessions.Select(s => $"{s.SessionId}|{s.Status}|{s.ProjectPath}"));
                await SendResponseAsync(response, 200, string.IsNullOrEmpty(sessionList) ? "(no sessions)" : sessionList);
                return;
            }

            if (request.HttpMethod != "POST")
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                response.Close();
                return;
            }

            var body = await ReadRequestBodyAsync(request, ct);
            var payload = ParsePayload(body);
            var sessionId = payload.TryGetValue("sessionId", out var sid) ? sid : string.Empty;
            var projectPath = payload.TryGetValue("projectPath", out var pp) ? pp : string.Empty;
            var toolName = payload.TryGetValue("toolName", out var tn) ? tn : string.Empty;
            var agentId = payload.TryGetValue("agentId", out var aid) ? aid : string.Empty;
            // Standardized metadata (TASKS.md §3.4): the originating hook event
            // name, tool_use_id, SessionStart source, and Notification fields.
            var hookEvent = payload.TryGetValue("hookEvent", out var he) ? he : string.Empty;
            var toolUseId = payload.TryGetValue("toolUseId", out var tui) ? tui : string.Empty;
            var source = payload.TryGetValue("source", out var src) ? src : string.Empty;
            var message = payload.TryGetValue("message", out var msg) ? msg : string.Empty;
            var title = payload.TryGetValue("title", out var ttl) ? ttl : string.Empty;
            var notifType = payload.TryGetValue("type", out var nty) ? nty : string.Empty;

            if (string.IsNullOrEmpty(sessionId))
            {
                await SendResponseAsync(response, 400, "Missing sessionId");
                return;
            }

            var route = request.Url?.AbsolutePath.Trim('/').ToLowerInvariant() ?? "";
            HandleRoute(route, sessionId, projectPath, toolName, agentId,
                hookEvent, toolUseId, source, message, title, notifType);

            await SendResponseAsync(response, 200, "OK");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"HookServer error: {ex.Message}");
            try { await SendResponseAsync(response, 500, "Internal Server Error"); }
            catch { /* response already closed */ }
        }
    }

    private void HandleRoute(string route, string sessionId, string projectPath,
        string toolName = "", string agentId = "",
        string hookEvent = "", string toolUseId = "", string source = "",
        string message = "", string title = "", string notifType = "")
    {
        switch (route)
        {
            case "start":
                // SessionStart: differentiate by source (TASKS.md §3.5).
                // compact is a continuation of the current session — preserve
                // state instead of resetting. startup/clear/resume start fresh.
                if (string.Equals(source, "compact", StringComparison.OrdinalIgnoreCase))
                {
                    _sessionManager.MarkCompacting(sessionId, projectPath);
                }
                else
                {
                    _sessionManager.AddSession(sessionId, projectPath);
                }
                break;
            case "busy":
                HandleBusy(sessionId, toolName, hookEvent, toolUseId);
                break;
            case "subagent-stop":
                // SubagentStop fires when a subagent finishes. If the payload
                // carries the subagent's agent_id, remove that exact row from
                // the Subagents collection so it disappears immediately (rather
                // than waiting for the watcher's stale-window to age it out).
                // Without agent_id, fall back to clearing the scalar flag and
                // let the watcher reconcile.
                if (!string.IsNullOrEmpty(agentId))
                    _sessionManager.RemoveSubagent(sessionId, agentId);
                else
                    _sessionManager.SetSubagentActive(sessionId, false);
                break;
            case "idle":
                // Stop / StopFailure: turn ended — clear any subagent flag too.
                HandleTurnEnd(sessionId, "stop");
                break;
            case "interactive":
                // Notification: conservatively keep current state unless the
                // notification is clearly a user-action-required prompt
                // (TASKS.md §3.3). Avoids flickering to Idle on background
                // notifications.
                HandleNotification(sessionId, message, title, notifType);
                break;
            case "stopfailure":
                // StopFailure fires when a turn ends on API error (rate_limit, etc.)
                HandleTurnEnd(sessionId, "stopfailure");
                break;
            case "end":
                _sessionManager.RemoveSession(sessionId);
                break;
            default:
                System.Diagnostics.Debug.WriteLine($"Unknown route: /{route}");
                break;
        }
    }

    /// <summary>
    /// Handle a /busy route event. The originating hook event name (§3.4)
    /// disambiguates UserPromptSubmit / PreToolUse / PostToolUse, which all
    /// share the busy endpoint. tool_use_id pairs Pre/PostToolUse for
    /// ActiveTools tracking (§3.2) and idempotency.
    /// </summary>
    private void HandleBusy(string sessionId, string toolName, string hookEvent, string toolUseId)
    {
        // PreToolUse with tool_name "Agent" means the main agent is launching a
        // subagent. The main agent then waits, so we mark a subagent as active
        // (main → Idle, subagent row → Working) instead of marking the main
        // session Busy. (Subagent handling is unchanged by this task.)
        if (string.Equals(toolName, "Agent", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(hookEvent, "PostToolUse", StringComparison.OrdinalIgnoreCase))
        {
            _sessionManager.SetSubagentActive(sessionId, true);
            return;
        }

        if (_sessionManager.IsSubagentActive(sessionId))
        {
            // A subagent is running, so the main agent is waiting — main stays
            // Idle. The main agent can fire PreToolUse / PostToolUse while a
            // subagent runs (internal activity, completion notifications,
            // subagent-result handling). Treating that as main Busy would
            // briefly turn the main indicator red even though the main agent is
            // idle. Do NOT clear the subagent flag here.
            _sessionManager.ResetBusyTimeout(sessionId);
            return;
        }

        // Dispatch by originating hook event (§3.4). Fall back to the legacy
        // toolName-based inference when hookEvent is absent (older hook proxy).
        if (string.IsNullOrEmpty(hookEvent))
        {
            _sessionManager.UpdateStatus(sessionId, SessionStatus.Busy);
            _sessionManager.ResetBusyTimeout(sessionId);
            return;
        }

        switch (hookEvent)
        {
            case "PreToolUse":
                // Track the in-flight tool so the watchdog applies its long
                // timeout tier (§3.2), then mark Busy.
                _sessionManager.TrackTool(sessionId, toolUseId);
                _sessionManager.UpdateStatus(sessionId, SessionStatus.Busy);
                _sessionManager.ResetBusyTimeout(sessionId);
                break;
            case "PostToolUse":
                // Tool finished — untrack it. Stay Busy (the turn continues);
                // just refresh the timer so the short tier reapplies if no
                // tools remain in flight.
                _sessionManager.UntrackTool(sessionId, toolUseId);
                _sessionManager.ResetBusyTimeout(sessionId);
                break;
            default:
                // UserPromptSubmit (or any other busy event) → Busy.
                _sessionManager.UpdateStatus(sessionId, SessionStatus.Busy);
                _sessionManager.ResetBusyTimeout(sessionId);
                break;
        }
    }

    /// <summary>
    /// Handle a Notification (§3.3). Only switch to Idle when the notification
    /// is recognizably a user-action-required prompt (permission, input). For
    /// background notifications or unclassifiable payloads, conservatively keep
    /// the current state and refresh the busy timer to avoid flicker. The raw
    /// payload fields are logged for later Phase-3 classification.
    /// </summary>
    private void HandleNotification(string sessionId, string message, string title, string notifType)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[CC-Pulse] notification: session={sessionId} type={notifType} title={title} message={message}");

        if (IsUserActionRequired(message, title, notifType))
        {
            _sessionManager.SetSubagentActive(sessionId, false);
            // The agent is blocked on a permission approval / input request —
            // mark it WaitingUser (a fine-grained Idle) so the UI shows
            // "waiting for input…" rather than a plain Idle. The flag is
            // cleared when the next Busy activity arrives (user approved).
            _sessionManager.SetWaitingUser(sessionId);
        }
        else
        {
            // Conservatively keep current state; just keep the timer alive.
            _sessionManager.ResetBusyTimeout(sessionId);
        }
    }

    /// <summary>
    /// Heuristic: does this Notification represent a prompt that blocks on user
    /// action (permission approval, input request)? Matches common Claude Code
    /// permission/notification wording. When uncertain, returns false so the
    /// state is preserved rather than flickering to Idle.
    /// </summary>
    private static bool IsUserActionRequired(string message, string title, string notifType)
    {
        var hay = $"{notifType}{title}{message}".ToLowerInvariant();
        // Permission approvals and explicit input requests block the turn.
        if (hay.Contains("permission") || hay.Contains("approve")
            || hay.Contains("allow") || hay.Contains("denied")
            || hay.Contains("waiting for your") || hay.Contains("needs your input")
            || hay.Contains("can you") && hay.Contains("?"))
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Handle Stop / StopFailure: the turn has ended. If tools are still marked
    /// in flight (a PostToolUse was lost), log the anomaly (§3.6) before
    /// resetting to Idle. Always clears subagent state and active tools.
    /// </summary>
    private void HandleTurnEnd(string sessionId, string reason)
    {
        if (_sessionManager.HasActiveOperations(sessionId))
        {
            _sessionManager.LogAnomaly(sessionId, $"stop_with_active_ops ({reason})");
        }
        _sessionManager.SetSubagentActive(sessionId, false);
        _sessionManager.ClearActiveTools(sessionId);
        _sessionManager.UpdateStatus(sessionId, SessionStatus.Idle);
    }

    private static async Task<string> ReadRequestBodyAsync(HttpListenerRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        return await reader.ReadToEndAsync(ct);
    }

    private static Dictionary<string, string> ParsePayload(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(body)) return result;

        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                result[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // If not valid JSON, ignore
        }

        return result;
    }

    private static async Task SendResponseAsync(HttpListenerResponse response, int statusCode, string message)
    {
        response.StatusCode = statusCode;
        response.ContentType = "text/plain";
        var buffer = Encoding.UTF8.GetBytes(message);
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer);
        response.Close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _listener.Close();
    }
}
