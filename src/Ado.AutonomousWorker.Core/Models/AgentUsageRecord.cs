using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Models;

/// <summary>A persisted token/cost record for one agent session within an execution.</summary>
public sealed record AgentUsageRecord(
    int Id,
    string ExecutionId,
    TaskPhase Phase,
    AgentRole Role,
    AgentProvider Provider,
    string? ActualModel,
    int InputTokens,
    int OutputTokens,
    int CachedInputTokens,
    decimal? CostUsd,
    DateTimeOffset Timestamp
);
