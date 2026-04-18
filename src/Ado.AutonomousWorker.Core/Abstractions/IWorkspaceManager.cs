using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Core.Abstractions;

public interface IWorkspaceManager
{
    Task PrepareAsync(CancellationToken cancellationToken);

    Task<RepoExecutionContext> CreateRepoExecutionAsync(
        int parentWorkItemId,
        int childTaskId,
        RepoKind repoKind,
        string slug,
        bool preserveExistingWorkspace,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Checks out an existing remote PR branch into a worktree. Unlike <see cref="CreateRepoExecutionAsync"/>,
    /// this does not create a new branch — it tracks the remote branch as-is.
    /// </summary>
    Task<RepoExecutionContext> CheckoutPrBranchAsync(
        int parentWorkItemId,
        int childTaskId,
        RepoKind repoKind,
        string branchName,
        bool preserveExistingWorkspace,
        CancellationToken cancellationToken
    );

    Task CleanupAsync(RepoExecutionContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Pulls the main branch of every configured repository so that planning agents see up-to-date code.
    /// Repositories whose <see cref="RepositoryProfile.BaseClonePath"/> does not exist are skipped.
    /// </summary>
    Task PullAllBaseClonesAsync(string artifactDirectory, CancellationToken cancellationToken);
}
