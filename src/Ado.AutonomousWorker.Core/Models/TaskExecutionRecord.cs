using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Models;

public sealed record TaskExecutionRecord(
    string ExecutionId,
    int WorkItemId,
    string Title,
    QueueItemKind Kind,
    TaskPhase Phase,
    ExecutionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ProviderPlanSessionId = null,
    int? PullRequestId = null
);
