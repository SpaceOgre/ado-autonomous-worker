using System.Text.Json;
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
            outputCallback: CreateOutputObserver(session, outputSchema is not null, outputCallback),
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
            outputCallback: CreateOutputObserver(session, structuredOutput: false, outputCallback),
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

        // Always use JSON output to capture actual model, tokens, and cost in the result envelope.
        // When a structured schema is required, also pass --json-schema for typed output.
        args.Add("--output-format");
        args.Add("json");
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

    // Parse the Claude CLI JSON result envelope that --output-format json always produces:
    // {"type":"result","subtype":"success","result":"<text>","model":"...","usage":{...},"total_cost_usd":0.001,...}
    // With --json-schema the envelope also contains "structured_output":{...}.
    private static (string Summary, string? StructuredPayload, AgentUsage? Usage) ParseEnvelope(string raw, bool hasSchema)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (raw, null, null);

        try
        {
            using var document = JsonDocument.Parse(raw.Trim());
            var root = document.RootElement;

            var summary = root.TryGetProperty("result", out var r) ? r.GetString()?.Trim() ?? raw.Trim() : raw.Trim();

            string? structuredPayload = null;
            if (hasSchema && root.TryGetProperty("structured_output", out var so))
                structuredPayload = so.GetRawText();

            var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
            int inputTokens = 0, outputTokens = 0, cachedTokens = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                inputTokens = usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
                outputTokens = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
                cachedTokens = usage.TryGetProperty("cache_read_input_tokens", out var ct) ? ct.GetInt32() : 0;
            }

            var costUsd = root.TryGetProperty("total_cost_usd", out var c) ? (decimal?)c.GetDecimal() : null;

            var agentUsage = (inputTokens > 0 || outputTokens > 0 || model is not null)
                ? new AgentUsage(model, inputTokens, outputTokens, cachedTokens, costUsd)
                : null;

            return (summary, structuredPayload, agentUsage);
        }
        catch (JsonException)
        {
            return (raw.Trim(), null, null);
        }
    }

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
        bool structuredOutput,
        Func<AgentOutputChunk, CancellationToken, Task>? outputCallback)
    {
        if (outputCallback is null)
        {
            return null;
        }

        long sequence = 0;
        return async (line, cancellationToken) =>
        {
            var chunks = AgentOutputNormalizer.NormalizeClaudeLine(line, structuredOutput);
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
