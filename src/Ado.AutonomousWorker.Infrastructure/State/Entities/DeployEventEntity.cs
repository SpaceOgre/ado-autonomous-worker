namespace Ado.AutonomousWorker.Infrastructure.State.Entities;

internal sealed class DeployEventEntity
{
    public int Id { get; set; }
    public string DeployId { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public string Message { get; set; } = "";
}
