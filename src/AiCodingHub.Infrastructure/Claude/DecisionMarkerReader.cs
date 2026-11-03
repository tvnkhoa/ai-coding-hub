using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AiCodingHub.Infrastructure.Claude;

/// <summary>A prompt the owner has on screen right now, reported by the optional hook.</summary>
public sealed record DecisionMarker(string SessionId, string? ToolUseId, string Tool, string? Header, string? Question, DateTimeOffset? At);

/// <summary>
/// Reads the markers that <c>tools/hooks/hud-decision.mjs</c> leaves while an AskUserQuestion /
/// ExitPlanMode prompt is open. Claude Code writes those tool calls to the transcript only after
/// they are answered, so the marker is the only live signal. Missing folder = no markers.
/// Read-only; never throws.
/// </summary>
public sealed class DecisionMarkerReader
{
    /// <summary>Markers older than this are leftovers from a crashed session and are ignored.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly string _dir;

    public DecisionMarkerReader(string? dir = null)
    {
        _dir = string.IsNullOrWhiteSpace(dir) ? DefaultDir : dir!;
    }

    public static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "hud-pending");

    public Dictionary<string, DecisionMarker> Read(DateTimeOffset now)
    {
        var result = new Dictionary<string, DecisionMarker>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(_dir))
            {
                return result;
            }

            foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
            {
                try
                {
                    if (now.UtcDateTime - File.GetLastWriteTimeUtc(file) > MaxAge)
                    {
                        continue;
                    }

                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var doc = JsonDocument.Parse(fs);
                    var root = doc.RootElement;
                    var sid = Str(root, "session_id") ?? Path.GetFileNameWithoutExtension(file);
                    DateTimeOffset? at = DateTimeOffset.TryParse(Str(root, "at"), out var t) ? t : null;
                    result[sid] = new DecisionMarker(sid, Str(root, "tool_use_id"), Str(root, "tool") ?? "AskUserQuestion",
                        Str(root, "header"), Str(root, "question"), at);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // Being rewritten or removed right now; next poll picks it up.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return result;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
