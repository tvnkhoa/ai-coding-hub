namespace AiCodingHub.Core.Options;

/// <summary>
/// Configuration for the subscription usage-limit probe. The 5-hour / weekly limits shown by
/// Claude Code's <c>/usage</c> are not written to any local file, so — when enabled — the app
/// reads them from the <c>anthropic-ratelimit-unified-*</c> response headers of a single minimal
/// <c>/v1/messages</c> call authenticated with the user's OAuth setup-token.
/// <para>
/// Disabled by default and never bundled with a token: paste a <c>claude setup-token</c>
/// (scope <c>user:inference</c>, starts with <c>sk-ant-oat01-</c>) into the per-user settings
/// file at <c>%APPDATA%\ClaudeMiniAlert\settings.json</c> under the "Usage" section.
/// </para>
/// </summary>
public sealed class UsageOptions
{
    public const string SectionName = "Usage";

    /// <summary>Master switch. When false (or no token) the probe never runs.</summary>
    public bool Enabled { get; set; }

    /// <summary>OAuth setup-token (<c>sk-ant-oat01-…</c>, scope <c>user:inference</c>).</summary>
    public string? OAuthToken { get; set; }

    /// <summary>
    /// When true (and no explicit <see cref="OAuthToken"/> is set), read the access token live from
    /// <c>~/.claude/.credentials.json</c>. Useful when <c>claude setup-token</c> is blocked (e.g. by
    /// enterprise SSO) — Claude Code keeps that token refreshed, so the probe self-renews.
    /// </summary>
    public bool UseCredentialsFile { get; set; }

    /// <summary>
    /// Optional override for the credentials-file location used when <see cref="UseCredentialsFile"/>
    /// is true. Empty/null means the default <c>%USERPROFILE%\.claude\.credentials.json</c>. Set this
    /// when the file lives elsewhere on the target machine (e.g. a different user profile or a copied
    /// file). Environment variables are expanded (e.g. <c>%USERPROFILE%\.claude\.credentials.json</c>).
    /// </summary>
    public string? CredentialsPath { get; set; }

    /// <summary>Minutes between probes. Kept high — each probe costs a request against quota.</summary>
    public int RefreshMinutes { get; set; } = 5;

    /// <summary>Cheapest model to poll with (one output token per call).</summary>
    public string PollModel { get; set; } = "claude-haiku-4-5-20251001";

    /// <summary>True when a token can be obtained — either supplied inline or read from credentials.</summary>
    public bool HasTokenSource =>
        !string.IsNullOrWhiteSpace(OAuthToken) || UseCredentialsFile;
}
