using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Models;

public sealed record TaskExecutionRecord(
    string ExecutionId,
    int WorkItemId,
    string Title,
    QueueItemKind Kind,
    TaskPhase Phase,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ProviderPlanSessionId = null);
