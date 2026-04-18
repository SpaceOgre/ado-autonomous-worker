namespace Ado.AutonomousWorker.Infrastructure.State.Entities;

internal sealed class ReviewResultEntity
{
    public int Id { get; set; }
    public string ExecutionId { get; set; } = "";
    public string Summary { get; set; } = "";
    public string TranscriptPath { get; set; } = "";
    public string? StructuredPayload { get; set; }
}
