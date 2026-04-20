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
        foreach (var repositoryExecution in context.RepositoryExecutions)
        {
            var childTask = repositoryExecution.ChildTask;
            var repoKind = repositoryExecution.RepoKind;
            var repoProfile = repositoryExecution.Profile;
            var workspace = repositoryExecution.Workspace;
            var artifactDirectory = repositoryExecution.ArtifactDirectory;

            await context.Journal.AppendEventAsync(
                context.ExecutionId,
                TaskPhase.Publishing,
                $"Creating commit and PR for {repoKind}.",
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
                ["commit", "-m", $"#{childTask.WorkItemId}: {context.WorkItem.Title}"],
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
                repositoryExecution,
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
                repoProfile.AzureDevOpsRepository,
                workspace.BranchName,
                repoProfile.MainBranch,
                prTitle,
                prDescription,
                cancellationToken
            );

            logger.LogInformation(
                "Execution {ExecutionId}: created draft PR !{PullRequestId} for {RepoKind}.",
                context.ExecutionId,
                pullRequest.PullRequestId,
                repoKind
            );

            if (workerOptions.Value.CleanupWorktreesAfterSuccess)
            {
                await workspaceManager.CleanupAsync(workspace, cancellationToken);
                logger.LogInformation(
                    "Execution {ExecutionId}: cleaned up worktree for {RepoKind}.",
                    context.ExecutionId,
                    repoKind
                );
            }

            await azureDevOpsClient.LinkPullRequestAsync(childTask.WorkItemId, pullRequest, cancellationToken);
            await stateStore.LinkPullRequestToExecutionAsync(
                context.ExecutionId,
                pullRequest.PullRequestId,
                repoProfile.AzureDevOpsRepository,
                cancellationToken
            );
            context.PublishedRepositories.Add(
                new RepositoryPublishResult(
                    repoKind,
                    childTask,
                    workspace.BranchName,
                    commitSha,
                    repoProfile.ValidationCommands.ToArray(),
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
        RepositoryExecution repoExecution,
        CancellationToken cancellationToken
    )
    {
        var workItem = context.WorkItem;
        var childTask = repoExecution.ChildTask;
        var workspace = repoExecution.Workspace;
        var repoKind = repoExecution.RepoKind;
        var plan = context.Plan;
        var mainBranch = repoExecution.Profile.MainBranch;
        var artifactDir = repoExecution.ArtifactDirectory;

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
                return DefaultPrMetadata(childTask, workItem);
            }

            var prompt = BuildPrDescriptionPrompt(
                workItem,
                childTask,
                repoKind.ToString(),
                plan,
                gitLog,
                diffStat,
                isBugfix
            );
            var config = agentOptions.Value.Pr;

            var runner = agentRunnerFactory.GetRunner(config.Provider);
            var result = await runner.StartSessionAsync(
                AgentRole.Plan,
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
                    AgentRole.Plan,
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
                        "Execution {ExecutionId}: PR description generated for {RepoKind}.",
                        context.ExecutionId,
                        repoKind
                    );
                    return (prPayload.Title, prPayload.Description);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Execution {ExecutionId}: PR description generation failed for {RepoKind}, using default.",
                context.ExecutionId,
                repoKind
            );
        }

        return DefaultPrMetadata(childTask, workItem);
    }

    private static (string Title, string Description) DefaultPrMetadata(
        ChildTaskRecord childTask,
        WorkItemDetails workItem
    ) => ($"#{childTask.WorkItemId}: {workItem.Title}", $"Resolved: #{childTask.WorkItemId}");

    private sealed record PrDescriptionPayload(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("description")] string? Description
    );

    private static string BuildPrDescriptionPrompt(
        WorkItemDetails workItem,
        ChildTaskRecord childTask,
        string repoKind,
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

        return $$"""
            Generate a pull request title and description for this {{repoKind}} change.
            Return JSON matching the provided schema exactly.

            Work item: #{{childTask.WorkItemId}} ({{workItem.WorkItemType}}) {{workItem.Title}}
            Classification: {{classification}}
            {{planLine}}

            Commits since main:
            {{gitLog}}

            Changed files (--stat):
            {{diffStat}}

            Output requirements:
            - "title": format exactly "#{{childTask.WorkItemId}}: <concise English description of this {{repoKind}} change>"
            - "description": use this exact markdown structure:

            #### PR Classification
            {{classification}}

            #### PR Summary
            <concrete summary of what changed and why — describe actual code changes, not planned work>

            **Key impacts:**
            - `<file or component>`: <what changed>
            - `<file or component>`: <what changed>
            {{bugSection}}
            Resolved: #{{childTask.WorkItemId}}

            Rules:
            - Keep everything in English.
            - Describe what was actually changed, not intended changes.
            - Be specific about files and components.
            - Keep the description concise but informative.
            """;
    }
}
