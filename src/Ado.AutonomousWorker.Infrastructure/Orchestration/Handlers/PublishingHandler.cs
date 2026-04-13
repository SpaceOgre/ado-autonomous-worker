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
    ProcessCommandRunner commandRunner,
    IOptions<WorkerOptions> workerOptions,
    ILogger<PublishingHandler> logger) : IPhaseHandler
{
    public TaskPhase Phase => TaskPhase.Publishing;

    public async Task<TaskPhase> ExecuteAsync(PhaseContext context, CancellationToken cancellationToken)
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
            await RunGitAsync(workspace.WorkingDirectory, artifactDirectory, "git-push.log",
                ["push", "-u", "origin", workspace.BranchName], cancellationToken);

            var pullRequest = await azureDevOpsClient.CreateDraftPullRequestAsync(
                repoProfile.AzureDevOpsRepository,
                workspace.BranchName,
                repoProfile.MainBranch,
                $"#{childTask.WorkItemId}: {context.WorkItem.Title}",
                $"Resolved: #{childTask.WorkItemId}",
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

        return TaskPhase.Completed;
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
