using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Models;

public sealed record QueueItem(
    int WorkItemId,
    WorkItemType WorkItemType,
    string Title,
    QueueItemKind Kind,
    double StackRank,
    int? Priority,
    string State,
    string? AssignedTo,
    WorkItemScope Scope = WorkItemScope.Fullstack,
    int? PullRequestId = null,
    string? PrRepositoryName = null,
    string? PrBranchName = null
);
