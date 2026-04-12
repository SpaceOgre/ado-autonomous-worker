using System.Text.Json;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Infrastructure.Execution;

namespace Ado.AutonomousWorker.Infrastructure.Agents;

public sealed class CodexRunner(ProcessCommandRunner commandRunner) : IAgentRunner
{
    public AgentProvider Provider => AgentProvider.Codex;

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
        var sessionId = Guid.NewGuid().ToString("N");
        var transcriptPath = Path.Combine(artifactDirectory, $"{role.ToString().ToLowerInvariant()}-codex.jsonl.log");
        var outputPath = Path.Combine(artifactDirectory, $"{role.ToString().ToLowerInvariant()}-codex.last.txt");
        var schemaPath = outputSchema is null ? null : Path.Combine(artifactDirectory, $"{role.ToString().ToLowerInvariant()}-codex-schema.json");
        var session = new AgentSessionHandle(sessionId, Provider, role, workingDirectory, transcriptPath, ProviderSessionId: null);

        if (schemaPath is not null)
        {
            await File.WriteAllTextAsync(schemaPath, outputSchema, cancellationToken);
        }

        var args = new List<string>
        {
            "exec",
            "--full-auto",
            "--json",
            "-m",
            config.Model,
            "-o",
            outputPath,
        };

        if (schemaPath is not null)
        {
            args.Add("--output-schema");
            args.Add(schemaPath);
        }

        AddConfigArguments(args, config);
        args.Add(prompt);

        var commandResult = await commandRunner.RunAsync(
            GetCommandPath(config, "codex"),
            args,
            workingDirectory,
            transcriptPath,
            environmentVariables: BuildEnvironment(["OPENAI_API_KEY", "OPENAI_ORG_ID", "OPENAI_BASE_URL"]),
            activityName: $"Codex {role}",
            outputCallback: CreateOutputObserver(session, outputCallback),
            cancellationToken);

        var payload = File.Exists(outputPath) ? await File.ReadAllTextAsync(outputPath, cancellationToken) : commandResult.StandardOutput;
        var providerSessionId = TryReadCodexSessionId(commandResult.StandardOutput);
        session = session with { ProviderSessionId = providerSessionId };
        var result = new AgentRunResult(commandResult.Success, SummarizePayload(payload), transcriptPath, commandResult.StandardOutput, payload);
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
        var transcriptPath = Path.Combine(artifactDirectory, $"{session.Role.ToString().ToLowerInvariant()}-codex-resume.log");
        var outputPath = Path.Combine(artifactDirectory, $"{session.Role.ToString().ToLowerInvariant()}-codex-resume.last.txt");
        var targetSession = session.ProviderSessionId ?? session.SessionId;

        var args = new List<string>
        {
            "exec",
            "resume",
            targetSession,
            "--json",
            "-m",
            config.Model,
            "-o",
            outputPath,
        };

        AddConfigArguments(args, config);
        args.Add(prompt);

        var commandResult = await commandRunner.RunAsync(
            GetCommandPath(config, "codex"),
            args,
            session.WorkingDirectory,
            transcriptPath,
            environmentVariables: BuildEnvironment(["OPENAI_API_KEY", "OPENAI_ORG_ID", "OPENAI_BASE_URL"]),
            activityName: $"Codex {session.Role} resume",
            outputCallback: CreateOutputObserver(session, outputCallback),
            cancellationToken);

        var payload = File.Exists(outputPath) ? await File.ReadAllTextAsync(outputPath, cancellationToken) : commandResult.StandardOutput;
        return new AgentRunResult(commandResult.Success, SummarizePayload(payload), transcriptPath, commandResult.StandardOutput, payload);
    }

    public async Task<ReviewResult> RunReviewAsync(
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

        return ReviewResultParser.Parse(startResult.Result.StructuredPayload, startResult.Result.TranscriptPath);
    }

    private static string? TryReadCodexSessionId(string stdout)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("type", out var typeProperty) &&
                    typeProperty.GetString() == "session_meta" &&
                    document.RootElement.TryGetProperty("payload", out var payload) &&
                    payload.TryGetProperty("id", out var idProperty))
                {
                    return idProperty.GetString();
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private static string GetCommandPath(AgentRoleConfig config, string fallback)
    {
        return string.IsNullOrWhiteSpace(config.CommandPath) ? fallback : config.CommandPath;
    }

    private static void AddConfigArguments(List<string> args, AgentRoleConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Effort))
        {
            args.Add("-c");
            args.Add($"model_reasoning_effort=\"{config.Effort}\"");
        }

        args.AddRange(config.ExtraArguments);
    }

    private static string SummarizePayload(string payload)
    {
        return string.IsNullOrWhiteSpace(payload) ? "No response captured from Codex." : payload.Trim();
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
            var chunks = AgentOutputNormalizer.NormalizeCodexLine(line);
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
