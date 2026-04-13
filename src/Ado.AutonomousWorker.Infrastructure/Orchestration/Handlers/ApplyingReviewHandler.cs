using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal sealed class ApplyingReviewHandler(
    IAgentRunnerFactory agentRunnerFactory,
    ExecutionJournal executionJournal,
    IOptions<AgentOptions> agentOptions,
    IOptions<StorageOptions> storageOptions,
    ILogger<ApplyingReviewHandler> logger) : IPhaseHandler
{
    public TaskPhase Phase => TaskPhase.ApplyingReview;

    public async Task<TaskPhase> ExecuteAsync(PhaseContext context, CancellationToken cancellationToken)
    {
        var codeSession = context.CodeSession
            ?? throw new InvalidOperationException("CodeSession must be set before entering ApplyingReview phase.");

        // On resume, AcceptedFindings may be null — re-derive from the persisted review result.
        var acceptedFindings = context.AcceptedFindings
            ?? context.LatestReview?.Findings.Where(ReviewFindingFilter.ShouldAccept).ToArray()
            ?? [];

        var repositoryExecutions = context.RepositoryExecutions;
        var scopeLabel = RepositoryWorkflowHelpers.FormatRepoKinds(repositoryExecutions.Select(r => r.RepoKind));
        var agentWorkingDirectory = RepositoryWorkflowHelpers.DetermineAgentWorkingDirectory(
            repositoryExecutions.Select(r => r.Workspace.WorkingDirectory));
        var implementationArtifactRoot = Path.Combine(
            storageOptions.Value.GetExecutionArtifactRoot(context.WorkItem.WorkItemId, context.ExecutionId), "implementation");

        await context.Journal.AppendEventAsync(
            context.ExecutionId, TaskPhase.ApplyingReview,
            $"Resuming coding agent to apply {acceptedFindings.Count} accepted findings for {scopeLabel}.",
            cancellationToken);

        logger.LogInformation(
            "Execution {ExecutionId}: applying {Count} review findings for {ScopeLabel}.",
            context.ExecutionId, acceptedFindings.Count, scopeLabel);

        var config = agentOptions.Value.Code;
        await agentRunnerFactory.GetRunner(config.Provider).ResumeSessionAsync(
            codeSession,
            config,
            BuildReviewResumePrompt(acceptedFindings),
            Path.Combine(implementationArtifactRoot, "resume"),
            executionJournal.CreateOutputObserver(context.ExecutionId),
            cancellationToken);

        return TaskPhase.Validating;
    }

    private static string BuildReviewResumePrompt(IReadOnlyList<ReviewFinding> findings)
    {
        var formattedFindings = string.Join(
            Environment.NewLine,
            findings.Select(f => $"- [{f.Severity}] {f.Title}: {f.Rationale}"));

        return $$"""
        Apply the accepted review findings and leave the repository ready for validation.

        Accepted findings:
        {{formattedFindings}}

        Requirements:
        - Do not create commits, push branches, open pull requests, link work items, or publish changes.
        """;
    }
}
