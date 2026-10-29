using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

/// <summary>
/// One half (backend or frontend) in scope for an execution. Every half of an execution shares the same
/// <see cref="Workspace"/> worktree, branch and pull request.
/// </summary>
internal sealed record RepositoryExecution(
    ChildTaskRecord ChildTask,
    RepositoryProfile Profile,
    RepoExecutionContext Workspace,
    string ArtifactDirectory
)
{
    public RepoKind RepoKind => Profile.Kind;

    public string FolderPath => Path.Combine(Workspace.WorkingDirectory, Profile.Folder);
}
