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

            if (string.IsNullOrEmpty(sessionId))
            {
                await SendResponseAsync(response, 400, "Missing sessionId");
                return;
            }

            var route = request.Url?.AbsolutePath.Trim('/').ToLowerInvariant() ?? "";
            HandleRoute(route, sessionId, projectPath, toolName, agentId);

            await SendResponseAsync(response, 200, "OK");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"HookServer error: {ex.Message}");
            try { await SendResponseAsync(response, 500, "Internal Server Error"); }
            catch { /* response already closed */ }
        }
    }

    private void HandleRoute(string route, string sessionId, string projectPath, string toolName = "", string agentId = "")
    {
        switch (route)
        {
            case "start":
                _sessionManager.AddSession(sessionId, projectPath);
                break;
            case "busy":
                // PreToolUse with tool_name "Agent" means the main agent is
                // launching a subagent. The main agent then waits, so we mark
                // a subagent as active (main → Idle, subagent row → Working)
                // instead of marking the main session Busy.
                if (string.Equals(toolName, "Agent", StringComparison.OrdinalIgnoreCase))
                {
                    _sessionManager.SetSubagentActive(sessionId, true);
                }
                else if (_sessionManager.IsSubagentActive(sessionId))
                {
                    // A subagent is running, so the main agent is waiting —
                    // main stays Idle. The main agent can fire PreToolUse /
                    // PostToolUse while a subagent runs (internal activity,
                    // completion notifications, subagent-result handling).
                    // Treating that as main Busy would briefly turn the main
                    // indicator red even though the main agent is idle. The
                    // SubagentWatcher also enforces this every poll, but
                    // suppressing it here avoids the flicker between polls.
                    // Do NOT clear the subagent flag here — subagent clearing
                    // is handled by SubagentStop, Stop/StopFailure, the 120s
                    // watchdog, or the watcher seeing no active subagents.
                    _sessionManager.ResetBusyTimeout(sessionId);
                }
                else
                {
                    // Main agent tool activity with no subagent running → Busy.
                    _sessionManager.UpdateStatus(sessionId, SessionStatus.Busy);
                    // Reset the watchdog timer on activity to keep the light red
                    // while Claude Code is actively working
                    _sessionManager.ResetBusyTimeout(sessionId);
                }
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
                _sessionManager.SetSubagentActive(sessionId, false);
                _sessionManager.UpdateStatus(sessionId, SessionStatus.Idle);
                break;
            case "interactive":
                // Notification (waiting for user input) → Idle (green)
                _sessionManager.SetSubagentActive(sessionId, false);
                _sessionManager.UpdateStatus(sessionId, SessionStatus.Idle);
                break;
            case "stopfailure":
                // StopFailure fires when a turn ends on API error (rate_limit, etc.)
                // The turn has ended, so set to Idle
                _sessionManager.SetSubagentActive(sessionId, false);
                _sessionManager.UpdateStatus(sessionId, SessionStatus.Idle);
                break;
            case "end":
                _sessionManager.RemoveSession(sessionId);
                break;
            default:
                System.Diagnostics.Debug.WriteLine($"Unknown route: /{route}");
                break;
        }
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
