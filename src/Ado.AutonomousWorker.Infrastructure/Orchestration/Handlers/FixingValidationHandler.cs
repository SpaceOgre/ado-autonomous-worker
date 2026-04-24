using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal sealed class FixingValidationHandler(
    IAgentRunnerFactory agentRunnerFactory,
    IStateStore stateStore,
    ExecutionJournal executionJournal,
    IOptions<AgentOptions> agentOptions,
    IOptions<StorageOptions> storageOptions,
    ILogger<FixingValidationHandler> logger
) : WorkItemHandler
{
    public override TaskPhase Phase => TaskPhase.FixingValidation;

    protected override async Task<PhaseTransition> ExecuteAsync(
        PhaseContext.WorkItemContext context,
        CancellationToken cancellationToken
    )
    {
        var codeSession =
            context.CodeSession
            ?? throw new InvalidOperationException("CodeSession must be set before entering FixingValidation phase.");

        var failures = context.ValidationFailures;
        if (failures.Count == 0)
            throw new InvalidOperationException("No validation failures recorded to fix.");

        var repositoryExecutions = context.RepositoryExecutions;
        var scopeLabel = RepositoryWorkflowHelpers.FormatRepoKinds(repositoryExecutions.Select(r => r.RepoKind));
        var implementationArtifactRoot = Path.Combine(
            storageOptions.Value.GetExecutionArtifactRoot(context.WorkItem.WorkItemId, context.ExecutionId),
            "implementation"
        );

        await context.Journal.AppendEventAsync(
            context.ExecutionId,
            TaskPhase.FixingValidation,
            $"Resuming coding agent to fix {failures.Count} validation failure(s) for {scopeLabel} (attempt {context.ValidationRetryCount}).",
            cancellationToken
        );

        logger.LogInformation(
            "Execution {ExecutionId}: fixing {Count} validation failure(s) for {ScopeLabel} (attempt {Attempt}).",
            context.ExecutionId,
            failures.Count,
            scopeLabel,
            context.ValidationRetryCount
        );

        var config = agentOptions.Value.Code;
        var resumeStart = await agentRunnerFactory
            .GetRunner(config.Provider)
            .ResumeSessionAsync(
                codeSession,
                config,
                BuildFixPrompt(failures),
                Path.Combine(implementationArtifactRoot, $"fix-validation-{context.ValidationRetryCount}"),
                executionJournal.CreateOutputObserver(context.ExecutionId),
                cancellationToken
            );

        await stateStore.SaveAgentSessionAsync(context.ExecutionId, resumeStart.Session, cancellationToken);
        if (resumeStart.Result.Usage is not null)
            await stateStore.RecordAgentUsageAsync(
                context.ExecutionId,
                TaskPhase.FixingValidation,
                AgentRole.Code,
                config.Provider,
                resumeStart.Result.Usage,
                cancellationToken
            );

        return PhaseTransition.To(TaskPhase.Validating);
    }

    internal static string BuildFixPrompt(IReadOnlyList<ValidationFailure> failures)
    {
        var sections = failures.Select(f =>
        {
            var output = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(f.StandardOutput))
                output.AppendLine(f.StandardOutput.Trim());
            if (!string.IsNullOrWhiteSpace(f.StandardError))
                output.AppendLine(f.StandardError.Trim());

            return $$"""
            [{{f.RepoKind}}] Command: {{f.Command}}
            Output:
            {{output.ToString().Trim()}}
            """;
        });

        return $$"""
            Validation failed after your changes. Fix the issues so that all validation commands pass.

            Failures:
            {{string.Join(Environment.NewLine + Environment.NewLine, sections)}}

            Requirements:
            - Fix all reported errors before finishing.
            - Run the validation commands yourself to confirm they pass before completing.
            - Do not create commits, push branches, open pull requests, link work items, or publish changes to Azure DevOps or Git hosting systems.
            """;
    }
}
