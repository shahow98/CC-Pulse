using System;
using System.Collections.Generic;

namespace ClaudeMonitor.Services;

/// <summary>
/// Simple localization lookup. All user-visible strings are defined here
/// in both English and Chinese. Call Lang.Get("Key") to retrieve the
/// string in the current language.
/// </summary>
public static class Lang
{
    private static readonly Dictionary<string, string> En = new()
    {
        // Status text
        ["StatusIdle"] = "Idle",
        ["StatusBusy"] = "Working…",
        ["StatusUnknown"] = "Unknown",

        // Status window
        ["NoActiveSessions"] = "No active sessions",

        // Tray
        ["TrayNoSessions"] = "CC-Pulse: No sessions",
        ["TraySessionStatus"] = "CC-Pulse: {0} ({1} session)",
        ["TraySessionStatusPlural"] = "CC-Pulse: {0} ({1} sessions)",
        ["TrayShowHide"] = "Show/Hide Window",
        ["TrayExit"] = "Exit",
        ["TrayLanguage"] = "Language",

        // Messages — Claude Code not found
        ["MsgClaudeNotFoundBody"] =
            "CC-Pulse requires Claude Code to be installed, but it was not detected on this system.\n\n" +
            "Please install Claude Code first: https://docs.anthropic.com/en/docs/claude-code\n\n" +
            "Do you want to continue anyway?",
        ["MsgClaudeNotFoundTitle"] = "Claude Code Not Found",

        // Messages — Hook configuration failed
        ["MsgHookConfigFailedBody"] =
            "CC-Pulse could not automatically configure Claude Code hooks.\n\n" +
            "You can manually run: ClaudeMonitor.exe configure-hooks\n\n" +
            "Or add hooks to: {0}",
        ["MsgHookConfigFailedTitle"] = "Hook Configuration Failed",

        // Messages — Hook configuration error
        ["MsgHookConfigErrorBody"] =
            "CC-Pulse hook auto-configuration error:\n\n{0}\n\n" +
            "You can manually run: ClaudeMonitor.exe configure-hooks",
        ["MsgHookConfigErrorTitle"] = "Hook Configuration Error",
    };

    private static readonly Dictionary<string, string> ZhCn = new()
    {
        // Status text
        ["StatusIdle"] = "空闲",
        ["StatusBusy"] = "工作中…",
        ["StatusUnknown"] = "未知",

        // Status window
        ["NoActiveSessions"] = "无活动会话",

        // Tray
        ["TrayNoSessions"] = "CC-Pulse: 无会话",
        ["TraySessionStatus"] = "CC-Pulse: {0} ({1} 个会话)",
        ["TraySessionStatusPlural"] = "CC-Pulse: {0} ({1} 个会话)",
        ["TrayShowHide"] = "显示/隐藏窗口",
        ["TrayExit"] = "退出",
        ["TrayLanguage"] = "语言",

        // Messages — Claude Code not found
        ["MsgClaudeNotFoundBody"] =
            "CC-Pulse 需要 Claude Code，但未在系统中检测到。\n\n" +
            "请先安装 Claude Code：https://docs.anthropic.com/en/docs/claude-code\n\n" +
            "是否仍要继续？",
        ["MsgClaudeNotFoundTitle"] = "未找到 Claude Code",

        // Messages — Hook configuration failed
        ["MsgHookConfigFailedBody"] =
            "CC-Pulse 无法自动配置 Claude Code Hook。\n\n" +
            "你可以手动运行：ClaudeMonitor.exe configure-hooks\n\n" +
            "或添加 Hook 到：{0}",
        ["MsgHookConfigFailedTitle"] = "Hook 配置失败",

        // Messages — Hook configuration error
        ["MsgHookConfigErrorBody"] =
            "CC-Pulse Hook 自动配置错误：\n\n{0}\n\n" +
            "你可以手动运行：ClaudeMonitor.exe configure-hooks",
        ["MsgHookConfigErrorTitle"] = "Hook 配置错误",
    };

    /// <summary>Get a localized string by key.</summary>
    public static string Get(string key)
    {
        var dict = GetCurrentDictionary();
        return dict.TryGetValue(key, out var value) ? value : key;
    }

    /// <summary>Get a localized format string by key, with arguments.</summary>
    public static string Get(string key, params object[] args)
    {
        var template = Get(key);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private static Dictionary<string, string> GetCurrentDictionary()
    {
        return AppSettings.Instance.Language == "zh-CN" ? ZhCn : En;
    }
}
