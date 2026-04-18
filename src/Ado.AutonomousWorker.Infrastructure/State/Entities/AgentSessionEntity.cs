namespace Ado.AutonomousWorker.Infrastructure.State.Entities;

internal sealed class AgentSessionEntity
{
    public int Id { get; set; }
    public string ExecutionId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public int Provider { get; set; }
    public int Role { get; set; }
    public string WorkDir { get; set; } = "";
    public string TranscriptPath { get; set; } = "";
    public string? ProviderSessionId { get; set; }
}
