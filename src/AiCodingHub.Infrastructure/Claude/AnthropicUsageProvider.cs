using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiCodingHub.Core.Abstractions;
using AiCodingHub.Core.Models;
using AiCodingHub.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiCodingHub.Infrastructure.Claude;

/// <summary>
/// Reads the subscription 5-hour / weekly usage limits by issuing one minimal
/// <c>POST /v1/messages</c> (1 output token) with the user's OAuth setup-token and parsing the
/// <c>anthropic-ratelimit-unified-*</c> response headers. Anthropic returns those headers even
/// on rate-limited (429) responses, so limits are read regardless of the call's status code.
/// <para>
/// The dedicated <c>/api/oauth/usage</c> endpoint is intentionally avoided — it 429s so
/// aggressively that polling it is unusable. The last good reading is cached and returned when a
/// probe fails, so a transient error does not blank the widget.
/// </para>
/// </summary>
public sealed class AnthropicUsageProvider : IUsageProvider
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";

    private readonly IOptionsMonitor<UsageOptions> _options;
    private readonly ILogger<AnthropicUsageProvider> _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Rebuilt when the configured credentials path changes (hot-reload).
    private ClaudeCredentialsReader? _credentials;
    private string? _credentialsPath;
    private bool _loggedMissing;

    private UsageInfo _last = UsageInfo.Empty;

    public AnthropicUsageProvider(IOptionsMonitor<UsageOptions> options, ILogger<AnthropicUsageProvider> log)
    {
        _options = options;
        _log = log;
    }

    public async Task<UsageInfo> GetAsync(CancellationToken cancellationToken = default)
    {
        var opt = _options.CurrentValue;
        if (!opt.Enabled || !opt.HasTokenSource)
        {
            return UsageInfo.Empty;
        }

        var token = ResolveToken(opt);
        if (string.IsNullOrWhiteSpace(token))
        {
            return UsageInfo.Empty;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.TryAddWithoutValidation("user-agent", "ClaudeMiniAlert/1.0");

            var model = string.IsNullOrWhiteSpace(opt.PollModel) ? "claude-haiku-4-5-20251001" : opt.PollModel;
            var body = $"{{\"model\":\"{model}\",\"max_tokens\":1,\"messages\":[{{\"role\":\"user\",\"content\":\".\"}}]}}";
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // Headers are present on success and on 429; parse either way.
            var parsed = ParseUnifiedHeaders(name =>
                response.Headers.TryGetValues(name, out var values)
                    ? string.Join(",", values)
                    : null);

            if (parsed.Available)
            {
                _last = parsed;
            }
            else
            {
                _log.LogDebug("Usage probe returned no unified rate-limit headers (status {Status})", (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Usage probe failed; keeping last known reading");
        }

        return _last;
    }

    /// <summary>
    /// Resolves the bearer token: an explicit inline token wins; otherwise, when enabled, the live
    /// access token from <c>~/.claude/.credentials.json</c>. Logs (does not fail) on an expired token.
    /// </summary>
    private string? ResolveToken(UsageOptions opt)
    {
        if (!string.IsNullOrWhiteSpace(opt.OAuthToken))
        {
            return opt.OAuthToken.Trim();
        }

        if (!opt.UseCredentialsFile)
        {
            return null;
        }

        var reader = GetCredentialsReader(opt.CredentialsPath);
        var (token, expiresAt) = reader.Read();

        if (string.IsNullOrWhiteSpace(token))
        {
            // Log once (not every probe) so a wrong/missing path is diagnosable without spamming.
            if (!_loggedMissing)
            {
                _loggedMissing = true;
                _log.LogWarning(
                    "Usage: no OAuth token from credentials file. Path='{Path}', exists={Exists}. " +
                    "Set Usage.CredentialsPath to the machine's .credentials.json, or ensure Claude Code is logged in.",
                    reader.ResolvedPath, reader.FileExists);
            }

            return null;
        }

        _loggedMissing = false;
        if (expiresAt is { } exp && exp <= DateTimeOffset.UtcNow)
        {
            _log.LogWarning(
                "Claude OAuth token in credentials file expired at {Expiry}; open Claude Code to refresh it",
                exp);
        }

        return token;
    }

    /// <summary>Returns a reader for the configured path, rebuilding it when the path changes.</summary>
    private ClaudeCredentialsReader GetCredentialsReader(string? path)
    {
        if (_credentials is null || !string.Equals(_credentialsPath, path, StringComparison.Ordinal))
        {
            _credentials = new ClaudeCredentialsReader(path);
            _credentialsPath = path;
            _loggedMissing = false;
        }

        return _credentials;
    }

    /// <summary>
    /// Pure parse of the <c>anthropic-ratelimit-unified-*</c> headers into a <see cref="UsageInfo"/>.
    /// Utilization headers are fractions in [0,1]; resets are Unix seconds. Exposed for testing.
    /// </summary>
    public static UsageInfo ParseUnifiedHeaders(Func<string, string?> get)
    {
        var have5h = TryFraction(get("anthropic-ratelimit-unified-5h-utilization"), out var fiveHour);
        var have7d = TryFraction(get("anthropic-ratelimit-unified-7d-utilization"), out var weekly);

        if (!have5h && !have7d)
        {
            return UsageInfo.Empty;
        }

        return new UsageInfo(
            Available: true,
            FiveHourPercent: fiveHour,
            FiveHourResetAt: TryUnixSeconds(get("anthropic-ratelimit-unified-5h-reset")),
            WeeklyPercent: weekly,
            WeeklyResetAt: TryUnixSeconds(get("anthropic-ratelimit-unified-7d-reset")),
            Status: get("anthropic-ratelimit-unified-5h-status"));
    }

    private static bool TryFraction(string? raw, out double percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(raw) ||
            !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
        {
            return false;
        }

        percent = Math.Clamp(f * 100.0, 0, 100);
        return true;
    }

    private static DateTimeOffset? TryUnixSeconds(string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw) &&
            long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
        {
            return DateTimeOffset.FromUnixTimeSeconds(s);
        }

        return null;
    }
}
