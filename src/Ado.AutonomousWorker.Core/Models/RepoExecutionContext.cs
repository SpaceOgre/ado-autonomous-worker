using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Models;

public sealed record RepoExecutionContext(
    int ParentWorkItemId,
    int ChildTaskId,
    RepoKind RepoKind,
    string BranchName,
    string WorkingDirectory
);
