namespace Ado.AutonomousWorker.Infrastructure.State.Entities;

internal sealed class ExecutionPrLinkEntity
{
    public string ExecutionId { get; set; } = "";
    public int PullRequestId { get; set; }
}
