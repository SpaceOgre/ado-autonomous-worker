using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Models;

public sealed record ChildTaskRecord(
    int WorkItemId,
    RepoKind RepoKind,
    string Title,
    string State);
