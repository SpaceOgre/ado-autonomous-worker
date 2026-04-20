namespace Ado.AutonomousWorker.Infrastructure.State.Entities;

internal sealed class ExecutionPlanEntity
{
    public string ExecutionId { get; set; } = "";
    public int Scope { get; set; }
    public string RepositoriesJson { get; set; } = "";
    public string TestIntentJson { get; set; } = "";
    public string RisksJson { get; set; } = "";
    public string Summary { get; set; } = "";
}
