using System;
using System.Collections.Generic;
using System.IO;

namespace AiCodingHub.Infrastructure.Claude;

/// <summary>A session transcript on disk: <c>&lt;root&gt;\&lt;encoded-cwd&gt;\&lt;session-id&gt;.jsonl</c>.</summary>
public sealed record SessionFile(string Path, string SessionId, string ProjectDir, DateTime LastWriteUtc);

/// <summary>Enumerates session transcripts under the Claude projects root.</summary>
public static class SessionLocator
{
    /// <summary>Default root: <c>%USERPROFILE%\.claude\projects</c>.</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    /// <summary>
    /// Every <c>*.jsonl</c> directly under a project folder (the <c>subagents\</c> sub-folders hold
    /// child-agent transcripts, not sessions). Tolerates folders changing underneath.
    /// </summary>
    public static IReadOnlyList<SessionFile> EnumerateSessions(string root)
    {
        var result = new List<SessionFile>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return result;
        }

        try
        {
            foreach (var projectDir in new DirectoryInfo(root).EnumerateDirectories())
            {
                try
                {
                    foreach (var file in projectDir.EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly))
                    {
                        result.Add(new SessionFile(
                            file.FullName,
                            Path.GetFileNameWithoutExtension(file.Name),
                            projectDir.Name,
                            file.LastWriteTimeUtc));
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return result;
    }
}
