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

        var payload = commandResult.StandardOutput;
        var result = new AgentRunResult(commandResult.Success, payload.Trim(), transcriptPath, commandResult.StandardOutput, outputSchema is null ? null : payload);
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

        return new AgentRunResult(commandResult.Success, commandResult.StandardOutput.Trim(), transcriptPath, commandResult.StandardOutput, null);
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

        if (outputSchema is not null)
        {
            args.Add("--output-format");
            args.Add("json");
            args.Add("--json-schema");
            args.Add(outputSchema);
        }
        else
        {
            args.Add("--output-format");
            args.Add("text");
        }

        args.AddRange(config.ExtraArguments);
        args.Add("-p");
        args.Add(prompt);
        return args;
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
