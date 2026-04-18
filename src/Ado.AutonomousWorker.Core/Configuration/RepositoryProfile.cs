using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Configuration;

public sealed class RepositoryProfile
{
    public RepoKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    public string AzureDevOpsRepository { get; set; } = string.Empty;

    public string BaseClonePath { get; set; } = string.Empty;

    public string RemoteUrl { get; set; } = string.Empty;

    public string MainBranch { get; set; } = "main";

    public string WorktreeRoot { get; set; } = string.Empty;

    /// <summary>
    /// Subdirectory relative to the worktree root where validation commands should run.
    /// Leave empty to run from the worktree root.
    /// </summary>
    public string ValidationWorkingDirectory { get; set; } = string.Empty;

    public List<string> ValidationCommands { get; set; } = [];
}
