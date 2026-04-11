namespace Ado.AutonomousWorker.Core.Models;

public sealed record WorkItemSummary(
    int WorkItemId,
    string Title,
    string State,
    string? AssignedTo,
    double StackRank);
