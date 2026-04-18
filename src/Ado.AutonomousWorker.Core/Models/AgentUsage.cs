namespace Ado.AutonomousWorker.Core.Models;

/// <summary>Token and cost metadata captured from a single agent session.</summary>
public sealed record AgentUsage(
    string? ActualModel,
    int InputTokens,
    int OutputTokens,
    int CachedInputTokens,
    decimal? CostUsd
);
