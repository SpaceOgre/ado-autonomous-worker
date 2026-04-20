namespace Ado.AutonomousWorker.Infrastructure.State.Entities;

internal sealed class ExecutionChildTaskEntity
{
    public string ExecutionId { get; set; } = "";
    public int WorkItemId { get; set; }
    public string RepoKind { get; set; } = "";
    public string Title { get; set; } = "";
}
