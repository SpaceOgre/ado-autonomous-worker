using System.Text.Json;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Infrastructure.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal sealed class PublishingHandler(
    IAzureDevOpsClient azureDevOpsClient,
    IWorkspaceManager workspaceManager,
    IRulesEngine rulesEngine,
    IAgentRunnerFactory agentRunnerFactory,
    IStateStore stateStore,
    ProcessCommandRunner commandRunner,
    IOptions<AgentOptions> agentOptions,
    IOptions<WorkerOptions> workerOptions,
    ILogger<PublishingHandler> logger) : IPhaseHandler
{
    public TaskPhase Phase => TaskPhase.Publishing;

    public async Task<PhaseTransition> ExecuteAsync(PhaseContext context, CancellationToken cancellationToken)
    {
        foreach (var repositoryExecution in context.RepositoryExecutions)
        {
            var childTask = repositoryExecution.ChildTask;
            var repoKind = repositoryExecution.RepoKind;
            var repoProfile = repositoryExecution.Profile;
            var workspace = repositoryExecution.Workspace;
            var artifactDirectory = repositoryExecution.ArtifactDirectory;

            await context.Journal.AppendEventAsync(
                context.ExecutionId, TaskPhase.Publishing, $"Creating commit and PR for {repoKind}.", cancellationToken);

            await RunGitAsync(workspace.WorkingDirectory, artifactDirectory, "git-add.log", ["add", "-A"], cancellationToken);
            await RunGitAllowingNoChangesAsync(workspace.WorkingDirectory, artifactDirectory, "git-commit.log",
                ["commit", "-m", $"#{childTask.WorkItemId}: {context.WorkItem.Title}"], cancellationToken);
            var commitSha = await GetCommitShaAsync(workspace.WorkingDirectory, artifactDirectory, cancellationToken);

            // Generate PR title and description using the coding agent after the commit is made,
            // so the diff accurately reflects what was implemented.
            var (prTitle, prDescription) = await GeneratePrDescriptionAsync(context, repositoryExecution, cancellationToken);

            await RunGitAsync(workspace.WorkingDirectory, artifactDirectory, "git-push.log",
                ["push", "-u", "origin", workspace.BranchName], cancellationToken);

            var pullRequest = await azureDevOpsClient.CreateDraftPullRequestAsync(
                repoProfile.AzureDevOpsRepository,
                workspace.BranchName,
                repoProfile.MainBranch,
                prTitle,
                prDescription,
                cancellationToken);

            logger.LogInformation(
                "Execution {ExecutionId}: created draft PR #{PullRequestId} for {RepoKind}.",
                context.ExecutionId, pullRequest.PullRequestId, repoKind);

            if (workerOptions.Value.CleanupWorktreesAfterSuccess)
            {
                await workspaceManager.CleanupAsync(workspace, cancellationToken);
                logger.LogInformation("Execution {ExecutionId}: cleaned up worktree for {RepoKind}.",
                    context.ExecutionId, repoKind);
            }

            await azureDevOpsClient.LinkPullRequestAsync(childTask.WorkItemId, pullRequest, cancellationToken);
            context.PublishedRepositories.Add(new RepositoryPublishResult(
                repoKind, childTask, workspace.BranchName, commitSha,
                repoProfile.ValidationCommands.ToArray(), pullRequest));
        }

        await azureDevOpsClient.UpdateTagsAsync(
            context.WorkItem.WorkItemId, rulesEngine.BuildCompletionTags(context.WorkItem), cancellationToken);

        var review = context.LatestReview ?? new ReviewResult("No review available.", [], string.Empty);
        await azureDevOpsClient.AddCommentAsync(
            context.WorkItem.WorkItemId,
            WorkItemCommentFormatter.BuildSuccessComment(
                context.ExecutionId, context.PublishedRepositories,
                new ImplementationRunResult(review, AcceptedFindingCount: null)),
            cancellationToken);

        return PhaseTransition.Complete();
    }

    private async Task<(string Title, string Description)> GeneratePrDescriptionAsync(
        PhaseContext context,
        RepositoryExecution repoExecution,
        CancellationToken cancellationToken)
    {
        var workItem = context.WorkItem;
        var childTask = repoExecution.ChildTask;
        var workspace = repoExecution.Workspace;
        var repoKind = repoExecution.RepoKind;
        var plan = context.Plan;
        var mainBranch = repoExecution.Profile.MainBranch;
        var artifactDir = repoExecution.ArtifactDirectory;

        var isBugfix = workItem.WorkItemType == WorkItemType.Bug
                       || workspace.BranchName.StartsWith("bugfix/", StringComparison.OrdinalIgnoreCase);

        try
        {
            var logResult = await commandRunner.RunAsync(
                "git",
                ["-C", workspace.WorkingDirectory, "log", "--oneline", $"origin/{mainBranch}..HEAD"],
                workspace.WorkingDirectory,
                Path.Combine(artifactDir, "pr-git-log.log"),
                environmentVariables: null, activityName: null, outputCallback: null, cancellationToken);

            var diffResult = await commandRunner.RunAsync(
                "git",
                ["-C", workspace.WorkingDirectory, "diff", $"origin/{mainBranch}...HEAD", "--stat"],
                workspace.WorkingDirectory,
                Path.Combine(artifactDir, "pr-git-diff-stat.log"),
                environmentVariables: null, activityName: null, outputCallback: null, cancellationToken);

            var gitLog = logResult.Success ? logResult.StandardOutput.Trim() : string.Empty;
            var diffStat = diffResult.Success ? diffResult.StandardOutput.Trim() : string.Empty;

            if (string.IsNullOrWhiteSpace(gitLog) && string.IsNullOrWhiteSpace(diffStat))
            {
                logger.LogWarning(
                    "Execution {ExecutionId}: no git context available for PR description, using default.",
                    context.ExecutionId);
                return DefaultPrMetadata(childTask, workItem);
            }

            var prompt = BuildPrDescriptionPrompt(workItem, childTask, repoKind.ToString(), plan, gitLog, diffStat, isBugfix);
            var config = agentOptions.Value.Pr;

            var runner = agentRunnerFactory.GetRunner(config.Provider);
            var result = await runner.StartSessionAsync(
                AgentRole.Plan,
                config,
                prompt,
                workspace.WorkingDirectory,
                Path.Combine(artifactDir, "pr-description"),
                AgentOutputSchemas.PullRequestDescription,
                outputCallback: null,
                cancellationToken);

            if (result.Result.Usage is not null)
                await stateStore.RecordAgentUsageAsync(context.ExecutionId, TaskPhase.Publishing, AgentRole.Plan, config.Provider, result.Result.Usage, cancellationToken);

            if (result.Result.Success && !string.IsNullOrWhiteSpace(result.Result.StructuredPayload))
            {
                using var doc = JsonDocument.Parse(result.Result.StructuredPayload);
                var root = doc.RootElement;
                var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
                var description = root.TryGetProperty("description", out var d) ? d.GetString() : null;
                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(description))
                {
                    logger.LogInformation(
                        "Execution {ExecutionId}: PR description generated for {RepoKind}.",
                        context.ExecutionId, repoKind);
                    return (title, description);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Execution {ExecutionId}: PR description generation failed for {RepoKind}, using default.",
                context.ExecutionId, repoKind);
        }

        return DefaultPrMetadata(childTask, workItem);
    }

    private static (string Title, string Description) DefaultPrMetadata(
        ChildTaskRecord childTask, WorkItemDetails workItem) =>
        (
            $"#{childTask.WorkItemId}: {workItem.Title}",
            $"Resolved: #{childTask.WorkItemId}"
        );

    private static string BuildPrDescriptionPrompt(
        WorkItemDetails workItem,
        ChildTaskRecord childTask,
        string repoKind,
        PlanResult? plan,
        string gitLog,
        string diffStat,
        bool isBugfix)
    {
        var classification = isBugfix ? "Bugfix" : "New Feature";
        var bugSection = isBugfix
            ? """

              #### Bug description
              <what the bug was and how this change fixes it>

              """
            : string.Empty;

        var planLine = plan is not null
            ? $"Implementation summary: {plan.Summary}"
            : string.Empty;

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

    private async Task RunGitAsync(string workingDirectory, string artifactDirectory,
        string artifactName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            "git", ["-C", workingDirectory, .. arguments],
            workingDirectory, Path.Combine(artifactDirectory, artifactName),
            environmentVariables: null, activityName: null, outputCallback: null, cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException(result.StandardError);
    }

    private async Task RunGitAllowingNoChangesAsync(string workingDirectory, string artifactDirectory,
        string artifactName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            "git", ["-C", workingDirectory, .. arguments],
            workingDirectory, Path.Combine(artifactDirectory, artifactName),
            environmentVariables: null, activityName: null, outputCallback: null, cancellationToken);

        if (!result.Success &&
            !result.StandardOutput.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase) &&
            !result.StandardError.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(result.StandardError);
        }
    }

    private async Task<string> GetCommitShaAsync(string workingDirectory, string artifactDirectory,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            "git", ["-C", workingDirectory, "rev-parse", "HEAD"],
            workingDirectory, Path.Combine(artifactDirectory, "git-rev-parse.log"),
            environmentVariables: null, activityName: null, outputCallback: null, cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException(result.StandardError);

        return result.StandardOutput.Trim();
    }
}
