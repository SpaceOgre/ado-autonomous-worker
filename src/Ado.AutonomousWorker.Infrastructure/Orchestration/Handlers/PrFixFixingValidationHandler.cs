using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal sealed class PrFixFixingValidationHandler(
    IAgentRunnerFactory agentRunnerFactory,
    IStateStore stateStore,
    ExecutionJournal executionJournal,
    IOptions<AgentOptions> agentOptions,
    IOptions<StorageOptions> storageOptions,
    ILogger<PrFixFixingValidationHandler> logger
) : PrFixHandler
{
    public override TaskPhase Phase => TaskPhase.FixingValidation;

    protected override async Task<PhaseTransition> ExecuteAsync(
        PhaseContext.PrFixContext context,
        CancellationToken cancellationToken
    )
    {
        var codeSession =
            context.CodeSession
            ?? throw new InvalidOperationException("CodeSession must be set before entering FixingValidation phase.");

        var failures = context.ValidationFailures;
        if (failures.Count == 0)
            throw new InvalidOperationException("No validation failures recorded to fix.");

        var scopeLabel = RepositoryWorkflowHelpers.FormatRepoKinds(
            context.RepositoryExecutions.Select(r => r.RepoKind)
        );
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
                FixingValidationHandler.BuildFixPrompt(failures),
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
}
