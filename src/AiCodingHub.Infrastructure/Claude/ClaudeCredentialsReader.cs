using System;
using System.IO;
using System.Text.Json;

namespace AiCodingHub.Infrastructure.Claude;

/// <summary>
/// Reads the Claude Code OAuth access token from <c>~/.claude/.credentials.json</c>
/// (<c>claudeAiOauth.accessToken</c>). Claude Code refreshes this token and writes it back to
/// the file when it expires, so reading it live on each probe piggybacks on that refresh — no
/// separate refresh flow is needed. The file is only re-parsed when its last-write time changes.
/// <para>
/// The token is read for the sole purpose of authenticating the usage probe against
/// <c>api.anthropic.com</c> — its legitimate audience — and is never persisted or sent elsewhere.
/// </para>
/// </summary>
public sealed class ClaudeCredentialsReader
{
    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    private readonly string _path;
    private DateTime _lastWrite = DateTime.MinValue;
    private bool _loaded;
    private string? _token;
    private DateTimeOffset? _expiresAt;

    public ClaudeCredentialsReader(string? path = null) =>
        _path = string.IsNullOrWhiteSpace(path)
            ? DefaultPath
            : Environment.ExpandEnvironmentVariables(path);

    /// <summary>The resolved credentials-file path this reader looks at (for diagnostics).</summary>
    public string ResolvedPath => _path;

    /// <summary>Whether the credentials file currently exists on disk (for diagnostics).</summary>
    public bool FileExists => File.Exists(_path);

    /// <summary>Current access token and its expiry, or (null, null) when unavailable.</summary>
    public (string? Token, DateTimeOffset? ExpiresAt) Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return (_token, _expiresAt);
            }

            var t = File.GetLastWriteTimeUtc(_path);
            if (_loaded && t == _lastWrite)
            {
                return (_token, _expiresAt);
            }

            _lastWrite = t;
            _loaded = true;
            _token = null;
            _expiresAt = null;

            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) &&
                oauth.ValueKind == JsonValueKind.Object)
            {
                if (oauth.TryGetProperty("accessToken", out var a) && a.ValueKind == JsonValueKind.String)
                {
                    _token = a.GetString();
                }

                if (oauth.TryGetProperty("expiresAt", out var e) && e.TryGetInt64(out var ms))
                {
                    _expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                }
            }
        }
        catch
        {
            // malformed/locked file — keep the last known value
        }

        return (_token, _expiresAt);
    }
}
