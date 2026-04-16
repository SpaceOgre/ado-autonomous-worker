using System.Text.Json;
using System.Text.Json.Serialization;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Infrastructure.Execution;

namespace Ado.AutonomousWorker.Infrastructure.Agents;

public sealed class ClaudeRunner(ProcessCommandRunner commandRunner) : IAgentRunner
{
    public AgentProvider Provider => AgentProvider.Claude;

    public async Task<AgentSessionStartResult> StartSessionAsync(
        AgentRole role,
        AgentRoleConfig config,
        string prompt,
        string workingDirectory,
        string artifactDirectory,
        string? outputSchema,
        Func<AgentOutputChunk, CancellationToken, Task>? outputCallback,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(artifactDirectory);
        var sessionId = Guid.NewGuid().ToString();
        var transcriptPath = Path.Combine(artifactDirectory, $"{role.ToString().ToLowerInvariant()}-claude.log");
        var session = new AgentSessionHandle(sessionId, Provider, role, workingDirectory, transcriptPath, sessionId);

        var args = BuildArguments(role, config, prompt, outputSchema, sessionId, isResume: false);
        var commandResult = await commandRunner.RunAsync(
            GetCommandPath(config),
            args,
            workingDirectory,
            transcriptPath,
            environmentVariables: BuildEnvironment(["ANTHROPIC_API_KEY"]),
            activityName: $"Claude {role}",
            outputCallback: CreateOutputObserver(session, outputCallback),
            cancellationToken,
            timeoutSeconds: config.TimeoutSeconds > 0 ? config.TimeoutSeconds : null);

        var (summary, structuredPayload, usage) = ParseEnvelope(commandResult.StandardOutput, hasSchema: outputSchema is not null);
        if (usage is not null && usage.ActualModel is null)
            usage = usage with { ActualModel = config.Model };
        var result = new AgentRunResult(commandResult.Success, summary, transcriptPath, commandResult.StandardOutput, structuredPayload, usage);
        return new AgentSessionStartResult(session, result);
    }

    public async Task<AgentRunResult> ResumeSessionAsync(
        AgentSessionHandle session,
        AgentRoleConfig config,
        string prompt,
        string artifactDirectory,
        Func<AgentOutputChunk, CancellationToken, Task>? outputCallback,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(artifactDirectory);
        var transcriptPath = Path.Combine(artifactDirectory, $"{session.Role.ToString().ToLowerInvariant()}-claude-resume.log");
        var args = BuildArguments(session.Role, config, prompt, outputSchema: null, session.ProviderSessionId ?? session.SessionId, isResume: true);

        var commandResult = await commandRunner.RunAsync(
            GetCommandPath(config),
            args,
            session.WorkingDirectory,
            transcriptPath,
            environmentVariables: BuildEnvironment(["ANTHROPIC_API_KEY"]),
            activityName: $"Claude {session.Role} resume",
            outputCallback: CreateOutputObserver(session, outputCallback),
            cancellationToken,
            timeoutSeconds: config.TimeoutSeconds > 0 ? config.TimeoutSeconds : null);

        var (summary, _, usage) = ParseEnvelope(commandResult.StandardOutput, hasSchema: false);
        if (usage is not null && usage.ActualModel is null)
            usage = usage with { ActualModel = config.Model };
        return new AgentRunResult(commandResult.Success, summary, transcriptPath, commandResult.StandardOutput, null, usage);
    }

    public async Task<(ReviewResult Review, AgentUsage? Usage)> RunReviewAsync(
        AgentRoleConfig config,
        string prompt,
        string workingDirectory,
        string artifactDirectory,
        string? outputSchema,
        Func<AgentOutputChunk, CancellationToken, Task>? outputCallback,
        CancellationToken cancellationToken)
    {
        var startResult = await StartSessionAsync(
            AgentRole.Review,
            config,
            prompt,
            workingDirectory,
            artifactDirectory,
            outputSchema,
            outputCallback,
            cancellationToken);

        return (ReviewResultParser.Parse(startResult.Result.StructuredPayload, startResult.Result.TranscriptPath), startResult.Result.Usage);
    }

    private static List<string> BuildArguments(AgentRole role, AgentRoleConfig config, string prompt, string? outputSchema, string sessionId, bool isResume)
    {
        var args = new List<string>();

        if (isResume)
        {
            args.Add("--resume");
            args.Add(sessionId);
        }
        else
        {
            args.Add("--session-id");
            args.Add(sessionId);
        }

        args.Add("--print");
        args.Add("--model");
        args.Add(config.Model);
        args.Add("--effort");
        args.Add(MapEffort(config.Effort));
        args.Add("--permission-mode");
        args.Add("bypassPermissions");
        args.Add("--append-system-prompt");
        args.Add(AgentSystemPromptGuards.BuildSystemPromptAppend(role));

        // Use stream-json so Claude emits one JSONL event per line during execution, enabling
        // real-time streaming.  The final "type":"result" line carries the same summary, model,
        // usage, and cost fields as the old single-blob json format.
        // When a structured schema is required, also pass --json-schema for typed output.
        args.Add("--output-format");
        args.Add("stream-json");
        if (outputSchema is not null)
        {
            args.Add("--json-schema");
            args.Add(outputSchema);
        }

        args.AddRange(config.ExtraArguments);
        args.Add("-p");
        args.Add(prompt);
        return args;
    }

    // Parse the Claude CLI result envelope from --output-format stream-json JSONL output.
    // stream-json emits one JSON object per line; the final line is the result envelope:
    // {"type":"result","subtype":"success","result":"<text>","model":"...","usage":{...},"total_cost_usd":0.001,...}
    // With --json-schema the result line also contains "structured_output":{...}.
    // Falls back to treating the entire raw string as a single JSON object for compat.
    internal static (string Summary, string? StructuredPayload, AgentUsage? Usage) ParseEnvelope(string raw, bool hasSchema)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (raw, null, null);

        // Iterate in reverse: the result line is always last in stream-json output, so
        // scanning from the end finds it on the first iteration.
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!line.StartsWith('{'))
                continue;

            try
            {
                var envelope = JsonSerializer.Deserialize<ClaudeResultEnvelope>(line);
                if (envelope?.Type == "result")
                    return ExtractFromEnvelope(envelope, hasSchema, raw);
            }
            catch (JsonException)
            {
                // Skip malformed lines — the result line will be found on a later iteration.
            }
        }

        // Fallback: try the whole raw string as a single JSON blob.
        try
        {
            var envelope = JsonSerializer.Deserialize<ClaudeResultEnvelope>(raw.Trim());
            if (envelope is not null)
                return ExtractFromEnvelope(envelope, hasSchema, raw);
        }
        catch (JsonException)
        {
        }

        return (raw.Trim(), null, null);
    }

    private static (string Summary, string? StructuredPayload, AgentUsage? Usage) ExtractFromEnvelope(
        ClaudeResultEnvelope envelope, bool hasSchema, string raw)
    {
        var summary = envelope.Result?.Trim() ?? raw.Trim();

        string? structuredPayload = null;
        if (hasSchema && envelope.StructuredOutput.ValueKind != JsonValueKind.Undefined)
            structuredPayload = envelope.StructuredOutput.GetRawText();

        var inputTokens = envelope.Usage?.InputTokens ?? 0;
        var outputTokens = envelope.Usage?.OutputTokens ?? 0;
        var cachedTokens = envelope.Usage?.CacheReadInputTokens ?? 0;
        var agentUsage = (inputTokens > 0 || outputTokens > 0 || envelope.Model is not null)
            ? new AgentUsage(envelope.Model, inputTokens, outputTokens, cachedTokens, envelope.TotalCostUsd)
            : null;

        return (summary, structuredPayload, agentUsage);
    }

    private sealed record ClaudeResultEnvelope(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("result")] string? Result,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("usage")] ClaudeUsage? Usage,
        [property: JsonPropertyName("total_cost_usd")] decimal? TotalCostUsd,
        [property: JsonPropertyName("structured_output")] JsonElement StructuredOutput);

    private sealed record ClaudeUsage(
        [property: JsonPropertyName("input_tokens")] int InputTokens,
        [property: JsonPropertyName("output_tokens")] int OutputTokens,
        [property: JsonPropertyName("cache_read_input_tokens")] int CacheReadInputTokens);

    private static string GetCommandPath(AgentRoleConfig config)
    {
        return string.IsNullOrWhiteSpace(config.CommandPath) ? "claude" : config.CommandPath;
    }

    private static string MapEffort(string effort)
    {
        return effort.ToLowerInvariant() switch
        {
            "xhigh" => "max",
            "high" => "high",
            "medium" => "medium",
            "low" => "low",
            _ => "high",
        };
    }

    private static IReadOnlyDictionary<string, string?> BuildEnvironment(IEnumerable<string> names)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
        };

        foreach (var name in names)
        {
            environment[name] = Environment.GetEnvironmentVariable(name);
        }

        return environment;
    }

    private static Func<ProcessOutputLine, CancellationToken, Task>? CreateOutputObserver(
        AgentSessionHandle session,
        Func<AgentOutputChunk, CancellationToken, Task>? outputCallback)
    {
        if (outputCallback is null)
        {
            return null;
        }

        long sequence = 0;
        return async (line, cancellationToken) =>
        {
            var chunks = AgentOutputNormalizer.NormalizeClaudeLine(line);
            foreach (var chunk in chunks)
            {
                await outputCallback(
                    new AgentOutputChunk(
                        session.SessionId,
                        session.Provider,
                        session.Role,
                        session.WorkingDirectory,
                        line.Stream,
                        Interlocked.Increment(ref sequence),
                        DateTimeOffset.UtcNow,
                        chunk),
                    cancellationToken);
            }
        };
    }
}
