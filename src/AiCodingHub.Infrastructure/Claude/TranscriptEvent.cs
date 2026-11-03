using System;

namespace AiCodingHub.Infrastructure.Claude;

public enum EventRole
{
    Unknown,
    User,
    Assistant,
    Summary,
    System
}

/// <summary>
/// Token usage reported on an assistant turn. <see cref="CacheCreation"/> is the total cache
/// write; <see cref="CacheCreation1h"/> is the part written with the 1-hour TTL (priced at 2×
/// input instead of 1.25×).
/// </summary>
public sealed record UsageTokens(long Input, long Output, long CacheCreation, long CacheRead, long CacheCreation1h = 0)
{
    /// <summary>Cache write at the 5-minute TTL.</summary>
    public long CacheCreation5m => Math.Max(0, CacheCreation - CacheCreation1h);

    /// <summary>Approximate current prompt size (what counts against the context window).</summary>
    public long ContextTokens => Input + CacheCreation + CacheRead;

    public static UsageTokens Zero { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// A single parsed transcript line, reduced to just the fields the HUD derives from.
/// </summary>
public sealed record TranscriptEvent
{
    public EventRole Role { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public string? Model { get; init; }
    public UsageTokens? Usage { get; init; }
    public string? Cwd { get; init; }
    public string? GitBranch { get; init; }
    public string? Version { get; init; }

    /// <summary>Number of tool_use blocks in an assistant message.</summary>
    public int ToolUseCount { get; init; }

    /// <summary>Name of the last tool_use block (for task-state mapping).</summary>
    public string? LastToolName { get; init; }

    /// <summary>API message id. Claude Code writes one line per content block of a message,
    /// each repeating the same usage, so usage is counted once per (message id, request id).</summary>
    public string? MessageId { get; init; }

    public string? RequestId { get; init; }

    /// <summary>Key identifying the API response this line belongs to, or null when unknown.</summary>
    public string? ResponseKey => MessageId is null && RequestId is null ? null : $"{MessageId}|{RequestId}";

    public bool HasText { get; init; }
    public bool HasThinking { get; init; }

    /// <summary>A user message carrying a tool_result (not a human prompt).</summary>
    public bool IsToolResult { get; init; }

    /// <summary>A tool_result flagged is_error.</summary>
    public bool IsToolResultError { get; init; }
}
