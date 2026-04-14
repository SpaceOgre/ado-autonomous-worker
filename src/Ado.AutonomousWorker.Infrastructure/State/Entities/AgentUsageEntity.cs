namespace Ado.AutonomousWorker.Infrastructure.State.Entities;

internal sealed class AgentUsageEntity
{
    public int Id { get; set; }
    public string ExecutionId { get; set; } = string.Empty;
    public int Phase { get; set; }
    public int Role { get; set; }
    public int Provider { get; set; }
    public string? ActualModel { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CachedInputTokens { get; set; }

    /// <summary>Stored as invariant-culture string to avoid floating-point round-trip loss.</summary>
    public string? CostUsd { get; set; }

    public DateTimeOffset Timestamp { get; set; }
}
