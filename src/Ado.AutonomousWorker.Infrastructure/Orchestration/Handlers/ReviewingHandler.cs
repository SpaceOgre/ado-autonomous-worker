using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Infrastructure.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal sealed class ReviewingHandler(
    IAgentRunnerFactory agentRunnerFactory,
    IStateStore stateStore,
    ExecutionJournal executionJournal,
    IOptions<AgentOptions> agentOptions,
    IOptions<StorageOptions> storageOptions,
    IOptions<WorkerOptions> workerOptions,
    ILogger<ReviewingHandler> logger) : IPhaseHandler
{
    public TaskPhase Phase => TaskPhase.Reviewing;

    public async Task<TaskPhase> ExecuteAsync(PhaseContext context, CancellationToken cancellationToken)
    {
        var workItem = context.WorkItem;
        var repositoryExecutions = context.RepositoryExecutions;
        var scopeLabel = RepositoryWorkflowHelpers.FormatRepoKinds(repositoryExecutions.Select(r => r.RepoKind));
        var agentWorkingDirectory = RepositoryWorkflowHelpers.DetermineAgentWorkingDirectory(
            repositoryExecutions.Select(r => r.Workspace.WorkingDirectory));
        var implementationArtifactRoot = Path.Combine(
            storageOptions.Value.GetExecutionArtifactRoot(workItem.WorkItemId, context.ExecutionId), "implementation");
        var commentsBlock = WorkItemPromptContextFormatter.FormatComments(
            workItem.Comments, workerOptions.Value.MaxPromptComments, workerOptions.Value.MaxPromptCommentCharacters);

        await context.Journal.AppendEventAsync(
            context.ExecutionId, TaskPhase.Reviewing, $"Running review agent for {scopeLabel}.", cancellationToken);

        logger.LogInformation("Execution {ExecutionId}: running review agent for {ScopeLabel}.", context.ExecutionId, scopeLabel);

        var config = agentOptions.Value.Review;
        var review = await agentRunnerFactory.GetRunner(config.Provider).RunReviewAsync(
            config,
            await PromptTemplateLoader.ApplyAsync(
                config,
                BuildReviewPrompt(workItem, repositoryExecutions, agentWorkingDirectory, commentsBlock),
                cancellationToken),
            agentWorkingDirectory,
            Path.Combine(implementationArtifactRoot, "review"),
            AgentOutputSchemas.Review,
            executionJournal.CreateOutputObserver(context.ExecutionId),
            cancellationToken);

        await stateStore.SaveReviewResultAsync(context.ExecutionId, review, cancellationToken);
        context.LatestReview = review;

        var acceptedFindings = review.Findings.Where(ReviewFindingFilter.ShouldAccept).ToArray();
        if (acceptedFindings.Length > 0)
        {
            context.AcceptedFindings = acceptedFindings;
            await context.Journal.AppendEventAsync(
                context.ExecutionId, TaskPhase.Reviewing,
                $"{acceptedFindings.Length} findings accepted for remediation.", cancellationToken);
            return TaskPhase.ApplyingReview;
        }

        await context.Journal.AppendEventAsync(
            context.ExecutionId, TaskPhase.Reviewing,
            "No accepted findings — proceeding to validation.", cancellationToken);
        return TaskPhase.Validating;
    }

    private static string BuildReviewPrompt(
        WorkItemDetails parent,
        IReadOnlyList<RepositoryExecution> repositoryExecutions,
        string agentWorkingDirectory,
        string commentsBlock)
    {
        var repositorySummary = RepositoryWorkflowHelpers.BuildRepositorySummary(repositoryExecutions, agentWorkingDirectory);
        return $$"""
        Review the coordinated changes across the listed repositories for correctness, regressions, integration mismatches, security issues, and missing tests.
        Return JSON that matches the supplied schema exactly.

        Parent item: #{{parent.WorkItemId}} {{RepositoryWorkflowHelpers.FormatWorkItemLabel(parent.WorkItemType)}} {{parent.Title}}
        Agent working directory: {{agentWorkingDirectory}}
        Parent feature: {{parent.ParentFeatureTitle ?? "-"}}

        Recent comments:
        {{commentsBlock}}

        Repositories:
        {{repositorySummary}}

        Requirements:
        - Do not create commits, push branches, open pull requests, link work items, or publish changes.
        """;
    }
}
