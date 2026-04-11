using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Models;

public sealed record QueueItem(
    int WorkItemId,
    string Title,
    QueueItemKind Kind,
    double StackRank,
    string State,
    string? AssignedTo);
