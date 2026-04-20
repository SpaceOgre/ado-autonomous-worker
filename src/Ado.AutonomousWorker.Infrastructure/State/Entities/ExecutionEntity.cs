namespace Ado.AutonomousWorker.Infrastructure.State.Entities;

internal sealed class ExecutionEntity
{
    public string ExecutionId { get; set; } = "";
    public int WorkItemId { get; set; }
    public string Title { get; set; } = "";
    public int Kind { get; set; }
    public int Phase { get; set; }
    public int Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? PlanProviderSessionId { get; set; }
}
