using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Models;

public sealed record WorkItemSummary(
    int WorkItemId,
    WorkItemType WorkItemType,
    string Title,
    string State,
    string? AssignedTo,
    double StackRank,
    int? Priority = null);
