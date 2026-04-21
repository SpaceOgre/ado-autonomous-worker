namespace Ado.AutonomousWorker.Infrastructure.State.Entities;

internal sealed class DeployEntity
{
    public string DeployId { get; set; } = "";
    public int Status { get; set; }
    public int Phase { get; set; }
    public string Ref { get; set; } = "";
    public bool IsTag { get; set; }
    public int? BackendPipelineId { get; set; }
    public int? BackendRunId { get; set; }
    public int? FrontendPipelineId { get; set; }
    public int? FrontendRunId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? FailureReason { get; set; }
}
