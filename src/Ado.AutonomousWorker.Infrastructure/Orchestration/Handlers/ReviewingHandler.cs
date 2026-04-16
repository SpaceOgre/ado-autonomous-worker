using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Infrastructure.Agents;
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
    ILogger<ReviewingHandler> logger) : WorkItemHandler
{
    public override TaskPhase Phase => TaskPhase.Reviewing;

    protected override async Task<PhaseTransition> ExecuteAsync(PhaseContext.WorkItemContext context, CancellationToken cancellationToken)
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
        var artifactDirectory = Path.Combine(implementationArtifactRoot, "review");
        var prompt = await PromptTemplateLoader.ApplyAsync(
            config,
            BuildReviewPrompt(workItem, repositoryExecutions, agentWorkingDirectory, commentsBlock, config.RequireStructuredOutput),
            cancellationToken);

        ReviewResult review;
        AgentUsage? reviewUsage;

        if (config.RequireStructuredOutput)
        {
            (review, reviewUsage) = await agentRunnerFactory.GetRunner(config.Provider).RunReviewAsync(
                config,
                prompt,
                agentWorkingDirectory,
                artifactDirectory,
                AgentOutputSchemas.Review,
                executionJournal.CreateOutputObserver(context.ExecutionId),
                cancellationToken);
        }
        else
        {
            // Free-text mode: skip --json-schema and parse the review agent's markdown output
            // directly. This supports agents such as the hardcore-code-reviewer Claude plugin
            // that produce a structured markdown report instead of JSON.
            var runner = agentRunnerFactory.GetRunner(config.Provider);
            var startResult = await runner.StartSessionAsync(
                AgentRole.Review,
                config,
                prompt,
                agentWorkingDirectory,
                artifactDirectory,
                outputSchema: null,
                executionJournal.CreateOutputObserver(context.ExecutionId),
                cancellationToken);
            review = ReviewResultParser.ParseFreeText(startResult.Result.Summary, startResult.Result.TranscriptPath);
            reviewUsage = startResult.Result.Usage;
        }

        await stateStore.SaveReviewResultAsync(context.ExecutionId, review, cancellationToken);
        if (reviewUsage is not null)
            await stateStore.RecordAgentUsageAsync(context.ExecutionId, TaskPhase.Reviewing, AgentRole.Review, config.Provider, reviewUsage, cancellationToken);
        context.LatestReview = review;

        var acceptedFindings = review.Findings.Where(ReviewFindingFilter.ShouldAccept).ToArray();
        if (acceptedFindings.Length > 0)
        {
            context.AcceptedFindings = acceptedFindings;
            await context.Journal.AppendEventAsync(
                context.ExecutionId, TaskPhase.Reviewing,
                $"{acceptedFindings.Length} findings accepted for remediation.", cancellationToken);
            return PhaseTransition.To(TaskPhase.ApplyingReview);
        }

        var belowThreshold = review.Findings.Count - acceptedFindings.Length;
        var noAcceptedMessage = belowThreshold > 0
            ? $"{belowThreshold} finding(s) below acceptance threshold — proceeding to validation."
            : "No findings — proceeding to validation.";
        await context.Journal.AppendEventAsync(
            context.ExecutionId, TaskPhase.Reviewing, noAcceptedMessage, cancellationToken);
        return PhaseTransition.To(TaskPhase.Validating);
    }

    private static string BuildReviewPrompt(
        WorkItemDetails parent,
        IReadOnlyList<RepositoryExecution> repositoryExecutions,
        string agentWorkingDirectory,
        string commentsBlock,
        bool structured)
    {
        var repositorySummary = RepositoryWorkflowHelpers.BuildRepositorySummary(repositoryExecutions, agentWorkingDirectory);
        var jsonInstruction = structured ? "\nReturn JSON that matches the supplied schema exactly.\n" : string.Empty;
        return $$"""
        Review the coordinated changes across the listed repositories for correctness, regressions, integration mismatches, security issues, and missing tests.
        {{jsonInstruction}}
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
