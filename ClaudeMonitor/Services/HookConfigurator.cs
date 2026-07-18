using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeMonitor.Services;

/// <summary>
/// Configures and removes CC-Pulse hooks in ~/.claude/settings.json.
/// Replaces the Python-based configure-hooks.cmd and remove-hooks.cmd scripts.
/// </summary>
public static class HookConfigurator
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "settings.json");

    /// <summary>
    /// CC-Pulse hook definitions: event -> list of (matcher, endpoint_suffix)
    /// SessionStart matchers correspond to how the session was initiated:
    ///   startup = new session, resume = --resume/--continue//resume,
    ///   clear = /clear, compact = auto or manual compaction
    /// </summary>
    private static readonly (string Matcher, string Suffix)[] SessionStartHooks =
        [("startup", "start"), ("resume", "start"), ("clear", "start"), ("compact", "start")];

    private static readonly (string Matcher, string Suffix)[] PreToolUseHooks =
        [("", "busy")];

    private static readonly (string Matcher, string Suffix)[] PostToolUseHooks =
        [("", "idle")];

    private static readonly (string Matcher, string Suffix)[] UserPromptSubmitHooks =
        [("", "busy")];

    private static readonly (string Matcher, string Suffix)[] StopHooks =
        [("", "idle")];

    private static readonly (string Matcher, string Suffix)[] SessionEndHooks =
        [("", "end")];

    private static readonly Dictionary<string, (string Matcher, string Suffix)[]> HooksConfig = new()
    {
        ["SessionStart"] = SessionStartHooks,
        ["PreToolUse"] = PreToolUseHooks,
        ["PostToolUse"] = PostToolUseHooks,
        ["UserPromptSubmit"] = UserPromptSubmitHooks,
        ["Stop"] = StopHooks,
        ["SessionEnd"] = SessionEndHooks,
    };

    /// <summary>
    /// Check whether CC-Pulse hooks are already configured in settings.json.
    /// Returns true if at least one CC-Pulse hook entry is found.
    /// </summary>
    public static bool AreHooksConfigured()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return false;

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonNode.Parse(json)?.AsObject();
            if (settings is null) return false;

            if (!settings.ContainsKey("hooks"))
                return false;

            var hooks = settings["hooks"]!.AsObject();

            foreach (var kvp in hooks)
            {
                var eventArray = kvp.Value?.AsArray();
                if (eventArray is null) continue;

                foreach (var entry in eventArray)
                {
                    var entryHooks = entry?["hooks"]?.AsArray();
                    if (entryHooks is null) continue;

                    foreach (var h in entryHooks)
                    {
                        var cmd = h?["command"]?.GetValue<string>() ?? "";
                        if (cmd.Contains("cc-pulse-hook") || cmd.Contains("ClaudeMonitor")
                                    || cmd.Contains("CC-Pulse-Hook"))
                            return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check whether CC-Pulse hooks are using the legacy shell-form format
    /// (e.g., "ClaudeMonitor.exe hook start" without args field).
    /// Returns true if any CC-Pulse hook uses the old format and should be migrated.
    /// </summary>
    public static bool UsesLegacyFormat()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return false;

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonNode.Parse(json)?.AsObject();
            if (settings is null) return false;

            if (!settings.ContainsKey("hooks"))
                return false;

            var hooks = settings["hooks"]!.AsObject();

            foreach (var kvp in hooks)
            {
                var eventArray = kvp.Value?.AsArray();
                if (eventArray is null) continue;

                foreach (var entry in eventArray)
                {
                    var entryHooks = entry?["hooks"]?.AsArray();
                    if (entryHooks is null) continue;

                    foreach (var h in entryHooks)
                    {
                        var cmd = h?["command"]?.GetValue<string>() ?? "";
                        var isCcPulse = cmd.Contains("cc-pulse-hook") || cmd.Contains("ClaudeMonitor")
                                    || cmd.Contains("CC-Pulse-Hook");
                        if (!isCcPulse) continue;

                        // Legacy format: shell form with "hook" in the command string and no "args" field
                        if (cmd.Contains(" hook ") && h?["args"] is null)
                            return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Add CC-Pulse hooks to Claude Code settings.
    /// Preserves all existing non-CC-Pulse hooks by appending into existing entries.
    /// Uses exec form (command + args) for reliable stdin pipe inheritance on Windows.
    /// </summary>
    public static int Configure(string hookExePath)
    {
        try
        {
            // Resolve the hook proxy path: same directory as the main exe, named CC-Pulse-Hook.exe
            var hookProxyPath = Path.Combine(
                Path.GetDirectoryName(hookExePath) ?? "",
                "CC-Pulse-Hook.exe");

            // If the proxy doesn't exist, fall back to the main exe (legacy mode)
            var useProxy = File.Exists(hookProxyPath);
            var effectivePath = useProxy ? hookProxyPath : hookExePath;

            // Convert to forward slashes for consistency
            var hookPath = effectivePath.Replace('\\', '/').TrimEnd('/');

            // Ensure .claude directory exists
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);

            // Load existing settings or create new
            JsonObject settings;
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                settings = JsonNode.Parse(json)?.AsObject() ?? [];
            }
            else
            {
                settings = [];
            }

            // Ensure hooks section exists
            if (!settings.ContainsKey("hooks"))
                settings["hooks"] = new JsonObject();

            var hooks = settings["hooks"]!.AsObject();

            foreach (var (eventName, entries) in HooksConfig)
            {
                if (!hooks.ContainsKey(eventName))
                    hooks[eventName] = new JsonArray();

                var eventArray = hooks[eventName]!.AsArray();

                foreach (var (matcher, suffix) in entries)
                {
                    // Use exec form (command + args) for reliable stdin pipe inheritance.
                    // Shell form ("command": "\"path\" hook start") goes through cmd.exe
                    // which may not properly pipe stdin to GUI subsystem executables.
                    JsonObject ccPulseHook;
                    if (useProxy)
                    {
                        ccPulseHook = new JsonObject
                        {
                            ["type"] = "command",
                            ["command"] = hookPath,
                            ["args"] = new JsonArray { suffix },
                            ["timeout"] = 5,
                        };
                    }
                    else
                    {
                        // Legacy fallback: shell form with main exe
                        ccPulseHook = new JsonObject
                        {
                            ["type"] = "command",
                            ["command"] = $"\"{hookPath}\" hook {suffix}",
                            ["timeout"] = 5,
                        };
                    }

                    // Search for an existing entry with the same matcher
                    JsonNode? existingEntry = null;
                    foreach (var entry in eventArray)
                    {
                        if (entry?["matcher"]?.GetValue<string>() == matcher)
                        {
                            existingEntry = entry;
                            break;
                        }
                    }

                    if (existingEntry is not null)
                    {
                        // Check if CC-Pulse hook already exists in this entry
                        var existingHooks = existingEntry["hooks"]?.AsArray();
                        bool alreadyExists = false;
                        if (existingHooks is not null)
                        {
                            foreach (var h in existingHooks)
                            {
                                var cmd = h?["command"]?.GetValue<string>() ?? "";
                                if (cmd.Contains("cc-pulse-hook") || cmd.Contains("ClaudeMonitor")
                                    || cmd.Contains("CC-Pulse-Hook"))
                                {
                                    alreadyExists = true;
                                    break;
                                }
                            }
                        }

                        if (!alreadyExists)
                        {
                            if (existingHooks is null)
                            {
                                existingEntry["hooks"] = new JsonArray();
                                existingHooks = existingEntry["hooks"]!.AsArray();
                            }
                            existingHooks.Add(ccPulseHook);
                        }
                    }
                    else
                    {
                        // No entry with this matcher — create a new one
                        eventArray.Add(new JsonObject
                        {
                            ["matcher"] = matcher,
                            ["hooks"] = new JsonArray { ccPulseHook },
                        });
                    }
                }
            }

            // Write back
            // Write JSON with indentation using Utf8JsonWriter to avoid
            // JsonSerializerOptions read-only issues in single-file publish
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                settings.WriteTo(writer);
            }
            File.WriteAllText(SettingsPath, Encoding.UTF8.GetString(stream.ToArray()));

            Console.WriteLine($"CC-Pulse hooks configured in {SettingsPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to configure Claude Code hooks automatically.");
            Console.Error.WriteLine($"You can manually add hooks to {SettingsPath}");
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Remove CC-Pulse hooks from Claude Code settings.
    /// Only removes individual CC-Pulse hook entries, preserving all other hooks.
    /// </summary>
    public static int Remove()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return 0;

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonNode.Parse(json)?.AsObject();
            if (settings is null) return 0;

            if (!settings.ContainsKey("hooks"))
                return 0;

            var hooks = settings["hooks"]!.AsObject();

            // Remove only CC-Pulse hooks from each event
            var eventsToClean = new List<string>();
            foreach (var kvp in hooks)
                eventsToClean.Add(kvp.Key);
            foreach (var eventName in eventsToClean)
            {
                var eventArray = hooks[eventName]?.AsArray();
                if (eventArray is null) continue;

                foreach (var entry in eventArray)
                {
                    if (entry is null) continue;
                    var entryHooks = entry["hooks"]?.AsArray();
                    if (entryHooks is null) continue;

                    // Remove hooks containing 'cc-pulse-hook' or 'ClaudeMonitor'
                    var toRemove = new List<JsonNode>();
                    foreach (var h in entryHooks)
                    {
                        var cmd = h?["command"]?.GetValue<string>() ?? "";
                        if (cmd.Contains("cc-pulse-hook") || cmd.Contains("ClaudeMonitor")
                                    || cmd.Contains("CC-Pulse-Hook"))
                            toRemove.Add(h!);
                    }
                    foreach (var h in toRemove)
                        entryHooks.Remove(h);
                }

                // Remove entries that now have empty hooks arrays
                var emptyEntries = new List<JsonNode>();
                foreach (var entry in eventArray)
                {
                    var entryHooks = entry?["hooks"]?.AsArray();
                    if (entryHooks is null || entryHooks.Count == 0)
                        emptyEntries.Add(entry!);
                }
                foreach (var e in emptyEntries)
                    eventArray.Remove(e);
            }

            // Remove empty event arrays
            var emptyEvents = new List<string>();
            foreach (var kvp in hooks)
            {
                if (kvp.Value?.AsArray()?.Count == 0)
                    emptyEvents.Add(kvp.Key);
            }
            foreach (var key in emptyEvents)
                hooks.Remove(key);

            // Remove empty hooks object
            if (hooks.Count == 0)
                settings.Remove("hooks");

            // Write back
            // Write JSON with indentation using Utf8JsonWriter to avoid
            // JsonSerializerOptions read-only issues in single-file publish
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                settings.WriteTo(writer);
            }
            File.WriteAllText(SettingsPath, Encoding.UTF8.GetString(stream.ToArray()));

            Console.WriteLine("CC-Pulse hooks removed from settings");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to remove CC-Pulse hooks: {ex.Message}");
            return 1;
        }
    }
}
