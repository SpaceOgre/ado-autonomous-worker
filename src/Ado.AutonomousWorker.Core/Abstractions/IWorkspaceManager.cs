using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Core.Abstractions;

public interface IWorkspaceManager
{
    Task PrepareAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates the execution's worktree on a new branch off <c>origin/{MainBranch}</c>. The branch is named after
    /// <paramref name="branchOwnerId"/>; backend and frontend changes share this one worktree.
    /// </summary>
    Task<RepoExecutionContext> CreateWorkspaceAsync(
        int parentWorkItemId,
        int branchOwnerId,
        string slug,
        bool preserveExistingWorkspace,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Checks out an existing remote PR branch into a worktree. Unlike <see cref="CreateWorkspaceAsync"/>,
    /// this does not create a new branch — it tracks the remote branch as-is. A clean worktree that already
    /// has the branch checked out is reused and fast-forwarded.
    /// </summary>
    Task<RepoExecutionContext> CheckoutPrBranchAsync(
        int parentWorkItemId,
        int branchOwnerId,
        string branchName,
        bool preserveExistingWorkspace,
        CancellationToken cancellationToken
    );

    Task CleanupAsync(RepoExecutionContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches <c>origin/{MainBranch}</c> and checks it out into a detached snapshot worktree so planning agents
    /// see up-to-date code without touching <see cref="RepositoryOptions.BaseClonePath"/>. Returns the snapshot path.
    /// </summary>
    Task<string> PreparePlanningSnapshotAsync(string artifactDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the halves whose folders contain changes on the workspace branch compared to
    /// <c>origin/{MainBranch}</c>, including uncommitted and untracked files.
    /// </summary>
    Task<IReadOnlyList<RepoKind>> GetChangedRepoKindsAsync(
        RepoExecutionContext context,
        string artifactDirectory,
        CancellationToken cancellationToken
    );
}
