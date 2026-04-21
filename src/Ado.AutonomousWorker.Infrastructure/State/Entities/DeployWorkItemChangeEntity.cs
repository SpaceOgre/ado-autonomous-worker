namespace Ado.AutonomousWorker.Infrastructure.State.Entities;

internal sealed class DeployWorkItemChangeEntity
{
    public int Id { get; set; }
    public string DeployId { get; set; } = "";
    public int WorkItemId { get; set; }
    public string WorkItemType { get; set; } = "";
    public string Title { get; set; } = "";
    public string FromState { get; set; } = "";
    public string ToState { get; set; } = "";
    public string? FromAssignee { get; set; }
    public string ToAssignee { get; set; } = "";
}
