namespace Ado.AutonomousWorker.Core.Models;

/// <summary>
/// The worktree an execution works in. It is shared by every half (backend/frontend) in scope;
/// <paramref name="BranchOwnerId"/> is the work item the branch is named after.
/// </summary>
public sealed record RepoExecutionContext(
    int ParentWorkItemId,
    int BranchOwnerId,
    string BranchName,
    string WorkingDirectory
);
