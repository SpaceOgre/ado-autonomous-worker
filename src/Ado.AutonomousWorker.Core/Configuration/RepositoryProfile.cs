using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Configuration;

/// <summary>One half (backend or frontend) of the monorepo described by <see cref="RepositoryOptions"/>.</summary>
public sealed class RepositoryProfile
{
    public RepoKind Kind { get; set; }

    /// <summary>Folder of this half relative to the repository root, e.g. <c>backend</c>.</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>
    /// Subdirectory relative to the worktree root where validation commands should run.
    /// Leave empty to run from the worktree root.
    /// </summary>
    public string ValidationWorkingDirectory { get; set; } = string.Empty;

    public List<string> ValidationCommands { get; set; } = [];
}
