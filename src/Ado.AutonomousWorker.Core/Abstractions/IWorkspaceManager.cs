using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Core.Abstractions;

public interface IWorkspaceManager
{
    Task PrepareAsync(CancellationToken cancellationToken);

    Task<RepoExecutionContext> CreateRepoExecutionAsync(int parentWorkItemId, int childTaskId, RepoKind repoKind, string slug, CancellationToken cancellationToken);

    Task CleanupAsync(RepoExecutionContext context, CancellationToken cancellationToken);
}
