using System;
using System.Text.Json;

namespace AiCodingHub.Infrastructure.Claude;

/// <summary>
/// Parses one JSONL transcript line into a <see cref="TranscriptEvent"/>. Tolerant: any
/// malformed/partial line returns null (the tailer only advances past complete lines).
/// </summary>
public sealed class TranscriptParser
{
    public TranscriptEvent? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var role = ReadRole(root);
            var ev = new TranscriptEvent
            {
                Role = role,
                Timestamp = ReadTimestamp(root),
                Cwd = ReadString(root, "cwd"),
                GitBranch = ReadString(root, "gitBranch"),
                Version = ReadString(root, "version"),
                RequestId = ReadString(root, "requestId"),
            };

            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            {
                return ev;
            }

            var model = ReadString(message, "model");
            var usage = ReadUsage(message);

            int toolUse = 0;
            string? lastTool = null;
            bool hasText = false, hasThinking = false, isToolResult = false, isToolResultError = false;

            if (message.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                {
                    hasText = true;
                }
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        if (block.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var type = ReadString(block, "type");
                        switch (type)
                        {
                            case "text":
                                hasText = true;
                                break;
                            case "thinking":
                            case "redacted_thinking":
                                hasThinking = true;
                                break;
                            case "tool_use":
                                toolUse++;
                                lastTool = ReadString(block, "name") ?? lastTool;
                                break;
                            case "tool_result":
                                isToolResult = true;
                                if (block.TryGetProperty("is_error", out var err) &&
                                    err.ValueKind == JsonValueKind.True)
                                {
                                    isToolResultError = true;
                                }
                                break;
                        }
                    }
                }
            }

            return ev with
            {
                MessageId = ReadString(message, "id"),
                Model = model,
                Usage = usage,
                ToolUseCount = toolUse,
                LastToolName = lastTool,
                HasText = hasText,
                HasThinking = hasThinking,
                IsToolResult = isToolResult,
                IsToolResultError = isToolResultError,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static EventRole ReadRole(JsonElement root)
    {
        var type = ReadString(root, "type");
        return type switch
        {
            "user" => EventRole.User,
            "assistant" => EventRole.Assistant,
            "summary" => EventRole.Summary,
            "system" => EventRole.System,
            _ => EventRole.Unknown,
        };
    }

    private static UsageTokens? ReadUsage(JsonElement message)
    {
        if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var cacheCreation = ReadLong(usage, "cache_creation_input_tokens");
        long cache1h = 0;
        if (usage.TryGetProperty("cache_creation", out var breakdown) && breakdown.ValueKind == JsonValueKind.Object)
        {
            cache1h = Math.Min(cacheCreation, ReadLong(breakdown, "ephemeral_1h_input_tokens"));
        }

        return new UsageTokens(
            ReadLong(usage, "input_tokens"),
            ReadLong(usage, "output_tokens"),
            cacheCreation,
            ReadLong(usage, "cache_read_input_tokens"),
            cache1h);
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static long ReadLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n
            : 0;

    private static DateTimeOffset? ReadTimestamp(JsonElement root)
    {
        var raw = ReadString(root, "timestamp");
        return DateTimeOffset.TryParse(raw, out var ts) ? ts : null;
    }
}
