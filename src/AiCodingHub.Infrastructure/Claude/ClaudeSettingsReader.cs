using System;
using System.IO;
using System.Text.Json;

namespace AiCodingHub.Infrastructure.Claude;

/// <summary>
/// Reads the user's Claude Code settings (<c>~/.claude/settings.json</c>) to discover the
/// selected model alias — in particular whether the 1M-context beta is active (e.g.
/// <c>"opus[1m]"</c>).
/// <para>
/// This is the authoritative local signal for the context-window size: the session transcript
/// records the bare model id (<c>claude-opus-4-8</c>) <b>without</b> the <c>[1m]</c> marker, so
/// the window cannot be inferred from the transcript alone. The file is only re-parsed when its
/// last-write time changes, so polling this on every refresh is cheap.
/// </para>
/// </summary>
public sealed class ClaudeSettingsReader
{
    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    private readonly string _path;
    private DateTime _lastWrite = DateTime.MinValue;
    private bool _largeContext;
    private string? _modelAlias;
    private bool _loaded;

    private static readonly string[] Families = { "opus", "sonnet", "haiku", "fable", "mythos" };

    public ClaudeSettingsReader(string? path = null) => _path = path ?? DefaultPath;

    /// <summary>
    /// True when the selected model enables the 1M-context beta (marker <c>[1m]</c> or <c>-1m</c>).
    /// Returns the last known value when the file is missing or unreadable.
    /// </summary>
    public bool IsLargeContext()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return _largeContext;
            }

            var t = File.GetLastWriteTimeUtc(_path);
            if (_loaded && t == _lastWrite)
            {
                return _largeContext; // unchanged since last parse
            }

            _lastWrite = t;
            _loaded = true;

            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            if (doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
            {
                var id = m.GetString()!.ToLowerInvariant();
                _modelAlias = id;
                _largeContext = id.Contains("[1m]") || id.Contains("-1m");
            }
            else
            {
                _modelAlias = null;
                _largeContext = false;
            }
        }
        catch
        {
            // malformed/locked file — keep the last known value
        }

        return _largeContext;
    }

    /// <summary>
    /// True when the 1M beta is selected <b>and</b> applies to <paramref name="sessionModel"/>:
    /// an alias naming a family (<c>opus[1m]</c>) only applies to sessions of that family, so a
    /// Haiku session is not stretched to 1M because the default model is Opus.
    /// </summary>
    public bool IsLargeContextFor(string? sessionModel)
    {
        if (!IsLargeContext())
        {
            return false;
        }

        var alias = _modelAlias ?? "";
        foreach (var family in Families)
        {
            if (alias.Contains(family, StringComparison.Ordinal))
            {
                return sessionModel?.Contains(family, StringComparison.OrdinalIgnoreCase) == true;
            }
        }

        return true; // full model id or unknown alias — assume it applies
    }
}
