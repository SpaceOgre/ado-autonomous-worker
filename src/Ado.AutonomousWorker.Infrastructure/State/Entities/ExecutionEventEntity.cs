namespace Ado.AutonomousWorker.Infrastructure.State.Entities;

internal sealed class ExecutionEventEntity
{
    public int Id { get; set; }
    public string ExecutionId { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public int Phase { get; set; }
    public string Message { get; set; } = "";
}
