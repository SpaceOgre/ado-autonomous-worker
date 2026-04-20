using System.Text.Json;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Infrastructure.Execution;

namespace Ado.AutonomousWorker.Infrastructure.Agents;

public static class AgentOutputNormalizer
{
    public static IReadOnlyList<string> NormalizeClaudeLine(ProcessOutputLine line)
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

        if (LooksLikeJson(content))
        {
            return NormalizeClaudeJsonLine(content);
        }

        return [content];
    }

    // Normalize a single line from --output-format stream-json output.
    // Each line is a JSON event; we extract human-readable text and discard bookkeeping events.
    private static IReadOnlyList<string> NormalizeClaudeJsonLine(string content)
    {
        try
        {
            var root = JsonSerializer.Deserialize<JsonElement>(content);
            var type = TryGetString(root, "type");

            return type switch
            {
                // The assistant event carries text the model is writing and tool calls it is making.
                "assistant" => NormalizeAssistantMessage(root),
                // system events include bookkeeping (init) and subagent task_progress updates.
                "system" => NormalizeSystemEvent(root),
                // result (final envelope) and user (tool results) carry no human-readable progress.
                "result" or "user" => [],
                // Unknown event shape — fall through to the generic key extractor.
                _ => NormalizeClaudeStructuredJson(content),
            };
        }
        catch (JsonException)
        {
            return [content];
        }
    }

    // Extract displayable text from a Claude stream-json "assistant" event.
    // The event shape is:
    //   {"type":"assistant","message":{"content":[{"type":"text","text":"..."},{"type":"tool_use","name":"Bash","input":{...}}]}}
    private static IReadOnlyList<string> NormalizeAssistantMessage(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message))
            return [];

        if (!message.TryGetProperty("content", out var content))
            return [];

        var messages = new List<string>();

        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                var itemType = TryGetString(item, "type");
                switch (itemType)
                {
                    case "text":
                        AddIfPresent(messages, TryGetString(item, "text"));
                        break;
                    case "tool_use":
                        var toolName = TryGetString(item, "name");
                        if (!string.IsNullOrWhiteSpace(toolName))
                        {
                            var detail = GetToolDetail(toolName, item);
                            messages.Add(string.IsNullOrWhiteSpace(detail)
                                ? $"[{toolName}]"
                                : $"[{toolName}] {detail}");
                        }
                        break;
                }
            }
        }

        return messages.Select(m => m.Trim()).Where(m => !string.IsNullOrWhiteSpace(m)).ToArray();
    }

    // Emit the description from task_progress events (subagent progress updates).
    // All other system subtypes (init, task_started, etc.) are bookkeeping — skip them.
    private static IReadOnlyList<string> NormalizeSystemEvent(JsonElement root)
    {
        var subtype = TryGetString(root, "subtype");
        if (subtype != "task_progress")
            return [];

        var description = TryGetString(root, "description");
        return string.IsNullOrWhiteSpace(description) ? [] : [$"  {description}"];
    }

    // Extract a concise display string from a tool_use input object.
    private static string? GetToolDetail(string toolName, JsonElement item)
    {
        if (!item.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return null;

        return toolName switch
        {
            "Glob" => TryGetString(input, "pattern"),
            "Grep" => FormatGrepDetail(input),
            "Read" => TryGetString(input, "file_path"),
            "Write" or "Edit" => TryGetString(input, "file_path"),
            "Bash" => Truncate(TryGetString(input, "command"), 120),
            "Agent" => TryGetString(input, "description"),
            "WebFetch" => TryGetString(input, "url"),
            "WebSearch" => TryGetString(input, "query"),
            _ => null,
        };
    }

    private static string? FormatGrepDetail(JsonElement input)
    {
        var pattern = TryGetString(input, "pattern");
        var path = TryGetString(input, "path");
        if (string.IsNullOrEmpty(pattern)) return path;
        if (string.IsNullOrEmpty(path)) return $"\"{pattern}\"";
        return $"\"{pattern}\" in {Path.GetFileName(path)}";
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength] + "…";

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
            var root = JsonSerializer.Deserialize<JsonElement>(content);
            var type = TryGetString(root, "type");
            if (ShouldIgnoreCodexType(type))
            {
                return [];
            }

            var payload = root.TryGetProperty("payload", out var payloadElement) ? payloadElement : root;

            var messages = new List<string>();
            if (IsCommandType(type))
            {
                var command =
                    TryGetString(payload, "command") ?? TryGetString(payload, "cmd") ?? TryGetString(payload, "title");

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
        var content = payload;
        if (payload.ValueKind == JsonValueKind.Object)
        {
            if (!payload.TryGetProperty("content", out content))
            {
                return;
            }
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
        return (value.StartsWith('{') && value.EndsWith('}')) || (value.StartsWith('[') && value.EndsWith(']'));
    }

    private static IReadOnlyList<string> NormalizeClaudeStructuredJson(string content)
    {
        try
        {
            var root = JsonSerializer.Deserialize<JsonElement>(content);
            var messages = new List<string>();

            AddIfPresent(messages, TryGetString(root, "message"));
            AddIfPresent(messages, TryGetString(root, "summary"));
            AddIfPresent(messages, TryGetString(root, "text"));
            AddIfPresent(messages, TryGetString(root, "title"));
            AddIfPresent(messages, TryGetString(root, "delta"));
            AddIfPresent(messages, TryGetString(root, "completion"));
            AddIfPresent(messages, TryGetNestedString(root, "error", "message"));
            AddContentStrings(root, messages);

            if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
            {
                AddIfPresent(messages, TryGetString(payload, "message"));
                AddIfPresent(messages, TryGetString(payload, "summary"));
                AddIfPresent(messages, TryGetString(payload, "text"));
                AddIfPresent(messages, TryGetString(payload, "title"));
                AddIfPresent(messages, TryGetString(payload, "delta"));
                AddIfPresent(messages, TryGetNestedString(payload, "error", "message"));
                AddContentStrings(payload, messages);
            }

            return messages
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool ShouldIgnoreCodexType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return false;
        }

        return type
            is "session_meta"
                or "token_count"
                or "usage"
                or "turn_started"
                or "turn_completed"
                or "trace"
                or "debug";
    }

    private static bool IsCommandType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return false;
        }

        return type.Contains("command", StringComparison.OrdinalIgnoreCase)
            || type.Contains("tool", StringComparison.OrdinalIgnoreCase);
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
