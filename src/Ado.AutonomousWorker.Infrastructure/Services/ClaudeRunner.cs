using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Core.Services;
using Ado.AutonomousWorker.Infrastructure.Execution;

namespace Ado.AutonomousWorker.Infrastructure.Services;

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
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(artifactDirectory);
        var sessionId = Guid.NewGuid().ToString();
        var transcriptPath = Path.Combine(artifactDirectory, $"{role.ToString().ToLowerInvariant()}-claude.log");

        var args = BuildArguments(config, prompt, outputSchema, sessionId, isResume: false);
        var commandResult = await commandRunner.RunAsync(
            GetCommandPath(config),
            args,
            workingDirectory,
            transcriptPath,
            environmentVariables: BuildEnvironment(["ANTHROPIC_API_KEY"]),
            cancellationToken);

        var payload = commandResult.StandardOutput;
        var session = new AgentSessionHandle(sessionId, Provider, role, workingDirectory, transcriptPath, sessionId);
        var result = new AgentRunResult(commandResult.Success, payload.Trim(), transcriptPath, commandResult.StandardOutput, outputSchema is null ? null : payload);
        return new AgentSessionStartResult(session, result);
    }

    public async Task<AgentRunResult> ResumeSessionAsync(
        AgentSessionHandle session,
        AgentRoleConfig config,
        string prompt,
        string artifactDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(artifactDirectory);
        var transcriptPath = Path.Combine(artifactDirectory, $"{session.Role.ToString().ToLowerInvariant()}-claude-resume.log");
        var args = BuildArguments(config, prompt, outputSchema: null, session.ProviderSessionId ?? session.SessionId, isResume: true);

        var commandResult = await commandRunner.RunAsync(
            GetCommandPath(config),
            args,
            session.WorkingDirectory,
            transcriptPath,
            environmentVariables: BuildEnvironment(["ANTHROPIC_API_KEY"]),
            cancellationToken);

        return new AgentRunResult(commandResult.Success, commandResult.StandardOutput.Trim(), transcriptPath, commandResult.StandardOutput, null);
    }

    public async Task<ReviewResult> RunReviewAsync(
        AgentRoleConfig config,
        string prompt,
        string workingDirectory,
        string artifactDirectory,
        string? outputSchema,
        CancellationToken cancellationToken)
    {
        var startResult = await StartSessionAsync(
            AgentRole.Review,
            config,
            prompt,
            workingDirectory,
            artifactDirectory,
            outputSchema,
            cancellationToken);

        return ReviewResultParser.Parse(startResult.Result.StructuredPayload, startResult.Result.TranscriptPath);
    }

    private static List<string> BuildArguments(AgentRoleConfig config, string prompt, string? outputSchema, string sessionId, bool isResume)
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
}
