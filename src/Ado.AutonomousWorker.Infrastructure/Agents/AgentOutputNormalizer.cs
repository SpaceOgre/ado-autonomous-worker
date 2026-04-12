using System.Text.Json;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Infrastructure.Execution;

namespace Ado.AutonomousWorker.Infrastructure.Agents;

public static class AgentOutputNormalizer
{
    public static IReadOnlyList<string> NormalizeClaudeLine(ProcessOutputLine line, bool structuredOutput)
    {
        var content = line.Content.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        if (line.Stream == AgentOutputStream.Stderr)
        {
            return [$"[stderr] {content}"];
        }

        if (structuredOutput && LooksLikeJson(content))
        {
            return [];
        }

        return [content];
    }

    public static IReadOnlyList<string> NormalizeCodexLine(ProcessOutputLine line)
    {
        var content = line.Content.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        if (line.Stream == AgentOutputStream.Stderr)
        {
            return [$"[stderr] {content}"];
        }

        if (!LooksLikeJson(content))
        {
            return [content];
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var type = TryGetString(root, "type");
            if (ShouldIgnoreCodexType(type))
            {
                return [];
            }

            var payload = root.TryGetProperty("payload", out var payloadElement)
                ? payloadElement
                : root;

            var messages = new List<string>();
            if (IsCommandType(type))
            {
                var command = TryGetString(payload, "command") ??
                              TryGetString(payload, "cmd") ??
                              TryGetString(payload, "title");

                if (!string.IsNullOrWhiteSpace(command))
                {
                    messages.Add($"Running: {command}");
                }
            }

            AddIfPresent(messages, TryGetString(payload, "message"));
            AddIfPresent(messages, TryGetString(payload, "summary"));
            AddIfPresent(messages, TryGetString(payload, "text"));
            AddIfPresent(messages, TryGetString(payload, "delta"));
            AddIfPresent(messages, TryGetString(payload, "title"));
            AddIfPresent(messages, TryGetNestedString(payload, "error", "message"));
            AddContentStrings(payload, messages);

            return messages
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException)
        {
            return [content];
        }
    }

    private static void AddContentStrings(JsonElement payload, List<string> messages)
    {
        if (!payload.TryGetProperty("content", out var content))
        {
            return;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            AddIfPresent(messages, content.GetString());
            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                AddIfPresent(messages, item.GetString());
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            AddIfPresent(messages, TryGetString(item, "text"));
            AddIfPresent(messages, TryGetString(item, "message"));
            AddIfPresent(messages, TryGetString(item, "value"));
            AddIfPresent(messages, TryGetString(item, "content"));
        }
    }

    private static bool LooksLikeJson(string value)
    {
        return (value.StartsWith('{') && value.EndsWith('}')) ||
               (value.StartsWith('[') && value.EndsWith(']'));
    }

    private static bool ShouldIgnoreCodexType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return false;
        }

        return type is "session_meta" or
            "token_count" or
            "usage" or
            "turn_started" or
            "turn_completed" or
            "trace" or
            "debug";
    }

    private static bool IsCommandType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return false;
        }

        return type.Contains("command", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("tool", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static string? TryGetNestedString(JsonElement element, string parentProperty, string childProperty)
    {
        if (!element.TryGetProperty(parentProperty, out var parent) || parent.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryGetString(parent, childProperty);
    }

    private static void AddIfPresent(List<string> messages, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            messages.Add(value);
        }
    }
}
