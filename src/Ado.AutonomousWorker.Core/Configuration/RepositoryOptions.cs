namespace Ado.AutonomousWorker.Core.Configuration;

/// <summary>
/// The monorepo the worker operates on. Backend and frontend are two halves of this one repository,
/// so an execution gets one worktree, one branch and one pull request whichever halves it touches.
/// </summary>
public sealed class RepositoryOptions
{
    public string AzureDevOpsRepository { get; set; } = string.Empty;

    /// <summary>
    /// Local clone that worktrees are created from. The worker only fetches here — it never pulls,
    /// checks out or otherwise touches the working tree — so this can be the developer's own checkout.
    /// </summary>
    public string BaseClonePath { get; set; } = string.Empty;

    public string MainBranch { get; set; } = "main";

    /// <summary>
    /// Folder that holds one worktree per branch, named after the branch without its prefix
    /// (<c>feature/31011_slug</c> → <c>{WorktreeRoot}\31011_slug</c>).
    /// </summary>
    public string WorktreeRoot { get; set; } = string.Empty;

    public RepositoryProfile Backend { get; set; } = new();

    public RepositoryProfile Frontend { get; set; } = new();
}
