using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

internal sealed record RepositoryExecution(
    ChildTaskRecord ChildTask,
    RepositoryProfile Profile,
    RepoExecutionContext Workspace,
    string ArtifactDirectory
)
{
    public RepoKind RepoKind => Workspace.RepoKind;
}
