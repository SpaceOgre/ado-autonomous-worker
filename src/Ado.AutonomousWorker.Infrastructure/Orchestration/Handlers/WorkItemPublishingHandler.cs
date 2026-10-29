using System.Text.Json;
using System.Text.Json.Serialization;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Infrastructure.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal sealed class WorkItemPublishingHandler(
    IAzureDevOpsClient azureDevOpsClient,
    IWorkspaceManager workspaceManager,
    IRulesEngine rulesEngine,
    IAgentRunnerFactory agentRunnerFactory,
    IStateStore stateStore,
    ExecutionJournal executionJournal,
    ProcessCommandRunner commandRunner,
    IOptions<AgentOptions> agentOptions,
    IOptions<RepositoryOptions> repositoryOptions,
    IOptions<StorageOptions> storageOptions,
    IOptions<WorkerOptions> workerOptions,
    ILogger<WorkItemPublishingHandler> logger
) : WorkItemHandler
{
    public override TaskPhase Phase => TaskPhase.Publishing;

    protected override async Task<PhaseTransition> ExecuteAsync(
        PhaseContext.WorkItemContext context,
        CancellationToken cancellationToken
    )
    {
        // Backend and frontend share one worktree and branch, so the whole change is one commit and one PR,
        // linked to every child task in scope.
        var repositoryExecutions = context.RepositoryExecutions;
        var workspace = RepositoryWorkflowHelpers.GetSharedWorkspace(repositoryExecutions);
        var childTasks = repositoryExecutions.Select(r => r.ChildTask).DistinctBy(t => t.WorkItemId).ToArray();
        var branchOwner =
            childTasks.FirstOrDefault(t => t.WorkItemId == workspace.BranchOwnerId)
            ?? RepositoryWorkflowHelpers.GetBranchOwner(childTasks);
        var scopeLabel = RepositoryWorkflowHelpers.FormatRepoKinds(repositoryExecutions.Select(r => r.RepoKind));
        var repository = repositoryOptions.Value;
        var artifactDirectory = Path.Combine(
            storageOptions.Value.GetExecutionArtifactRoot(context.WorkItem.WorkItemId, context.ExecutionId),
            "publish"
        );

        await context.Journal.AppendEventAsync(
            context.ExecutionId,
            TaskPhase.Publishing,
            $"Creating commit and PR for {scopeLabel}.",
            cancellationToken
        );

        await GitPublishHelper.RunGitAsync(
            commandRunner,
            workspace.WorkingDirectory,
            artifactDirectory,
            "git-add.log",
            ["add", "-A"],
            cancellationToken
        );
        await GitPublishHelper.RunGitAllowingNoChangesAsync(
            commandRunner,
            workspace.WorkingDirectory,
            artifactDirectory,
            "git-commit.log",
            ["commit", "-m", $"#{branchOwner.WorkItemId}: {context.WorkItem.Title}"],
            cancellationToken
        );
        var commitSha = await GitPublishHelper.GetCommitShaAsync(
            commandRunner,
            workspace.WorkingDirectory,
            artifactDirectory,
            cancellationToken
        );

        var (prTitle, prDescription) = await GeneratePrDescriptionAsync(
            context,
            workspace,
            branchOwner,
            childTasks,
            scopeLabel,
            artifactDirectory,
            cancellationToken
        );

        await GitPublishHelper.RunGitAsync(
            commandRunner,
            workspace.WorkingDirectory,
            artifactDirectory,
            "git-push.log",
            ["push", "-u", "origin", workspace.BranchName],
            cancellationToken
        );

        var pullRequest = await azureDevOpsClient.CreateDraftPullRequestAsync(
            repository.AzureDevOpsRepository,
            workspace.BranchName,
            repository.MainBranch,
            prTitle,
            prDescription,
            cancellationToken
        );

        logger.LogInformation(
            "Execution {ExecutionId}: created draft PR !{PullRequestId} for {ScopeLabel}.",
            context.ExecutionId,
            pullRequest.PullRequestId,
            scopeLabel
        );

        if (workerOptions.Value.CleanupWorktreesAfterSuccess)
        {
            await workspaceManager.CleanupAsync(workspace, cancellationToken);
            logger.LogInformation("Execution {ExecutionId}: cleaned up worktree.", context.ExecutionId);
        }

        foreach (var childTask in childTasks)
        {
            await azureDevOpsClient.LinkPullRequestAsync(childTask.WorkItemId, pullRequest, cancellationToken);
            await azureDevOpsClient.UpdateWorkItemStateAsync(childTask.WorkItemId, "Pull Request", cancellationToken);
        }

        await stateStore.LinkPullRequestToExecutionAsync(
            context.ExecutionId,
            pullRequest.PullRequestId,
            repository.AzureDevOpsRepository,
            cancellationToken
        );

        foreach (var repositoryExecution in repositoryExecutions)
        {
            context.PublishedRepositories.Add(
                new RepositoryPublishResult(
                    repositoryExecution.RepoKind,
                    repositoryExecution.ChildTask,
                    workspace.BranchName,
                    commitSha,
                    repositoryExecution.Profile.ValidationCommands.ToArray(),
                    pullRequest
                )
            );
        }

        await azureDevOpsClient.UpdateTagsAsync(
            context.WorkItem.WorkItemId,
            rulesEngine.BuildCompletionTags(context.WorkItem),
            cancellationToken
        );

        var review = context.LatestReview ?? new ReviewResult("No review available.", [], string.Empty);
        await azureDevOpsClient.AddCommentAsync(
            context.WorkItem.WorkItemId,
            WorkItemCommentFormatter.BuildSuccessComment(
                context.ExecutionId,
                context.PublishedRepositories,
                new ImplementationRunResult(review, AcceptedFindingCount: null)
            ),
            cancellationToken
        );

        return PhaseTransition.Complete();
    }

    private async Task<(string Title, string Description)> GeneratePrDescriptionAsync(
        PhaseContext.WorkItemContext context,
        RepoExecutionContext workspace,
        ChildTaskRecord branchOwner,
        IReadOnlyList<ChildTaskRecord> childTasks,
        string scopeLabel,
        string artifactDir,
        CancellationToken cancellationToken
    )
    {
        var workItem = context.WorkItem;
        var plan = context.Plan;
        var mainBranch = repositoryOptions.Value.MainBranch;

        var isBugfix =
            workItem.WorkItemType == WorkItemType.Bug
            || workspace.BranchName.StartsWith("bugfix/", StringComparison.OrdinalIgnoreCase);

        try
        {
            var logResult = await commandRunner.RunAsync(
                "git",
                ["-C", workspace.WorkingDirectory, "log", "--oneline", $"origin/{mainBranch}..HEAD"],
                workspace.WorkingDirectory,
                Path.Combine(artifactDir, "pr-git-log.log"),
                environmentVariables: null,
                activityName: null,
                outputCallback: null,
                cancellationToken
            );

            var diffResult = await commandRunner.RunAsync(
                "git",
                ["-C", workspace.WorkingDirectory, "diff", $"origin/{mainBranch}...HEAD", "--stat"],
                workspace.WorkingDirectory,
                Path.Combine(artifactDir, "pr-git-diff-stat.log"),
                environmentVariables: null,
                activityName: null,
                outputCallback: null,
                cancellationToken
            );

            var gitLog = logResult.Success ? logResult.StandardOutput.Trim() : string.Empty;
            var diffStat = diffResult.Success ? diffResult.StandardOutput.Trim() : string.Empty;

            if (string.IsNullOrWhiteSpace(gitLog) && string.IsNullOrWhiteSpace(diffStat))
            {
                logger.LogWarning(
                    "Execution {ExecutionId}: no git context available for PR description, using default.",
                    context.ExecutionId
                );
                return DefaultPrMetadata(branchOwner, childTasks, workItem);
            }

            var prompt = BuildPrDescriptionPrompt(
                workItem,
                branchOwner,
                childTasks,
                scopeLabel,
                plan,
                gitLog,
                diffStat,
                isBugfix
            );
            var config = agentOptions.Value.Pr;

            var runner = agentRunnerFactory.GetRunner(config.Provider);
            var result = await runner.StartSessionAsync(
                AgentRole.Pr,
                config,
                prompt,
                workspace.WorkingDirectory,
                Path.Combine(artifactDir, "pr-description"),
                AgentOutputSchemas.PullRequestDescription,
                executionJournal.CreateOutputObserver(context.ExecutionId),
                cancellationToken
            );

            await stateStore.SaveAgentSessionAsync(context.ExecutionId, result.Session, cancellationToken);
            if (result.Result.Usage is not null)
                await stateStore.RecordAgentUsageAsync(
                    context.ExecutionId,
                    TaskPhase.Publishing,
                    AgentRole.Pr,
                    config.Provider,
                    result.Result.Usage,
                    cancellationToken
                );

            if (result.Result.Success && !string.IsNullOrWhiteSpace(result.Result.StructuredPayload))
            {
                var prPayload = JsonSerializer.Deserialize<PrDescriptionPayload>(result.Result.StructuredPayload);
                if (!string.IsNullOrWhiteSpace(prPayload?.Title) && !string.IsNullOrWhiteSpace(prPayload?.Description))
                {
                    logger.LogInformation(
                        "Execution {ExecutionId}: PR description generated for {ScopeLabel}.",
                        context.ExecutionId,
                        scopeLabel
                    );
                    return (prPayload.Title, prPayload.Description);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Execution {ExecutionId}: PR description generation failed for {ScopeLabel}, using default.",
                context.ExecutionId,
                scopeLabel
            );
        }

        return DefaultPrMetadata(branchOwner, childTasks, workItem);
    }

    private static (string Title, string Description) DefaultPrMetadata(
        ChildTaskRecord branchOwner,
        IReadOnlyList<ChildTaskRecord> childTasks,
        WorkItemDetails workItem
    ) => ($"#{branchOwner.WorkItemId}: {workItem.Title}", FormatResolvedLine(childTasks));

    private static string FormatResolvedLine(IReadOnlyList<ChildTaskRecord> childTasks) =>
        $"Resolved: {string.Join(", ", childTasks.Select(t => $"#{t.WorkItemId}"))}";

    private sealed record PrDescriptionPayload(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("description")] string? Description
    );

    private static string BuildPrDescriptionPrompt(
        WorkItemDetails workItem,
        ChildTaskRecord branchOwner,
        IReadOnlyList<ChildTaskRecord> childTasks,
        string scopeLabel,
        PlanResult? plan,
        string gitLog,
        string diffStat,
        bool isBugfix
    )
    {
        var classification = isBugfix ? "Bugfix" : "New Feature";
        var bugSection = isBugfix
            ? """

                #### Bug description
                <what the bug was and how this change fixes it>

                """
            : string.Empty;

        var planLine = plan is not null ? $"Implementation summary: {plan.Summary}" : string.Empty;
        var resolvedLine = FormatResolvedLine(childTasks);

        return $$"""
            Generate a pull request title and description for this {{scopeLabel}} change in the monorepo.
            Return JSON matching the provided schema exactly.

            Work item: #{{branchOwner.WorkItemId}} ({{workItem.WorkItemType}}) {{workItem.Title}}
            Classification: {{classification}}
            {{planLine}}

            Commits since main:
            {{gitLog}}

            Changed files (--stat):
            {{diffStat}}

            Output requirements:
            - "title": format exactly "#{{branchOwner.WorkItemId}}: <concise English description of this change>"
            - "description": use this exact markdown structure:

            #### PR Classification
            {{classification}}

            #### PR Summary
            <concrete summary of what changed and why — describe actual code changes, not planned work>

            **Key impacts:**
            - `<file or component>`: <what changed>
            - `<file or component>`: <what changed>
            {{bugSection}}
            {{resolvedLine}}

            Rules:
            - Keep everything in English.
            - Describe what was actually changed, not intended changes.
            - Be specific about files and components.
            - Keep the description concise but informative.
            """;
    }
}
