using System;
using System.IO;

namespace ClaudeMonitor.Services;

/// <summary>
/// Resolves Claude Code on-disk paths (transcript JSONL files, subagent
/// directories) from a session id and project path. Claude Code encodes the
/// project path into the <c>~/.claude/projects/&lt;encoded&gt;</c> folder name
/// by replacing drive separators and path separators with '-'. e.g.
/// "C:\Users\foo\bar" -&gt; "C--Users-foo-bar".
/// </summary>
public static class ClaudePaths
{
    /// <summary>The ~/.claude/projects directory shared by all sessions.</summary>
    public static readonly string ProjectsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "projects");

    /// <summary>
    /// Encode a project path the way Claude Code does: replace drive separator
    /// and path separators with '-'. Returns null for a blank path.
    /// </summary>
    public static string? EncodeProjectPath(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        var chars = projectPath.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] == ':' || chars[i] == '\\' || chars[i] == '/')
                chars[i] = '-';
        }
        return new string(chars);
    }

    /// <summary>
    /// Resolve the main transcript JSONL path for a session:
    /// <c>~/.claude/projects/&lt;encoded&gt;/&lt;sessionId&gt;.jsonl</c>.
    /// Returns null if the project path cannot be encoded.
    /// </summary>
    public static string? ResolveTranscriptPath(string projectPath, string sessionId)
    {
        var encoded = EncodeProjectPath(projectPath);
        if (encoded is null) return null;
        return Path.Combine(ProjectsDir, encoded, sessionId + ".jsonl");
    }

    /// <summary>
    /// Resolve the subagents directory for a session:
    /// <c>~/.claude/projects/&lt;encoded&gt;/&lt;sessionId&gt;/subagents</c>.
    /// Returns null if the project path cannot be encoded.
    /// </summary>
    public static string? ResolveSubagentsDir(string projectPath, string sessionId)
    {
        var encoded = EncodeProjectPath(projectPath);
        if (encoded is null) return null;
        return Path.Combine(ProjectsDir, encoded, sessionId, "subagents");
    }
}
