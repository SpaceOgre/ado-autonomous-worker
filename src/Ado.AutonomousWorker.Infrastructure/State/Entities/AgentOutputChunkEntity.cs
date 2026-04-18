namespace Ado.AutonomousWorker.Infrastructure.State.Entities;

internal sealed class AgentOutputChunkEntity
{
    public int Id { get; set; }
    public string ExecutionId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public int Provider { get; set; }
    public int Role { get; set; }
    public string WorkDir { get; set; } = "";
    public int Stream { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string Content { get; set; } = "";
}
