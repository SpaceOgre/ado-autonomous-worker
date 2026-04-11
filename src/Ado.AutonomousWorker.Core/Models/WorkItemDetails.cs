using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Models;

public sealed record WorkItemDetails
{
    public int WorkItemId { get; init; }

    public string Title { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;

    public string? AssignedTo { get; init; }

    public string IterationPath { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string AcceptanceCriteria { get; init; } = string.Empty;

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<ChildTaskRecord> ChildTasks { get; init; } = [];

    public QueueItemKind Kind { get; init; }
}
