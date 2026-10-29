using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Infrastructure.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal sealed class PrFixPublishingHandler(
    IAzureDevOpsClient azureDevOpsClient,
    IWorkspaceManager workspaceManager,
    IRulesEngine rulesEngine,
    ProcessCommandRunner commandRunner,
    IOptions<StorageOptions> storageOptions,
    IOptions<WorkerOptions> workerOptions,
    ILogger<PrFixPublishingHandler> logger
) : PrFixHandler
{
    public override TaskPhase Phase => TaskPhase.Publishing;

    protected override async Task<PhaseTransition> ExecuteAsync(
        PhaseContext.PrFixContext context,
        CancellationToken cancellationToken
    )
    {
        var prTarget = context.PrTarget;
        var workspace = RepositoryWorkflowHelpers.GetSharedWorkspace(context.RepositoryExecutions);
        var artifactDirectory = Path.Combine(
            storageOptions.Value.GetExecutionArtifactRoot(context.WorkItem.WorkItemId, context.ExecutionId),
            "publish"
        );

        await context.Journal.AppendEventAsync(
            context.ExecutionId,
            TaskPhase.Publishing,
            $"Committing and pushing PR fix changes to branch '{workspace.BranchName}'.",
            cancellationToken
        );

        var commitMessage = context.CommitMessage ?? $"#{workspace.BranchOwnerId}: Address PR review comments";

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
            ["commit", "-m", commitMessage],
            cancellationToken
        );
        var commitSha = await GitPublishHelper.GetCommitShaAsync(
            commandRunner,
            workspace.WorkingDirectory,
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

        logger.LogInformation(
            "Execution {ExecutionId}: pushed PR fix commit {CommitSha} to '{BranchName}'.",
            context.ExecutionId,
            commitSha,
            workspace.BranchName
        );

        if (workerOptions.Value.CleanupWorktreesAfterSuccess)
        {
            await workspaceManager.CleanupAsync(workspace, cancellationToken);
            logger.LogInformation("Execution {ExecutionId}: cleaned up worktree after PR fix.", context.ExecutionId);
        }

        await azureDevOpsClient.RemovePullRequestLabelAsync(
            prTarget.RepositoryName,
            prTarget.PullRequestId,
            "AI-Rework",
            cancellationToken
        );

        await azureDevOpsClient.UpdateTagsAsync(
            context.WorkItem.WorkItemId,
            rulesEngine.BuildCompletionTags(context.WorkItem),
            cancellationToken
        );

        var shortSha = commitSha.Length > 8 ? commitSha[..8] : commitSha;
        await azureDevOpsClient.AddCommentAsync(
            context.WorkItem.WorkItemId,
            $"PR review comments addressed — pushed commit `{shortSha}` to `{workspace.BranchName}` (PR !{prTarget.PullRequestId}).",
            cancellationToken
        );

        return PhaseTransition.Complete();
    }
}
