namespace Ado.AutonomousWorker.Core.Models;

/// <summary>Represents a single work item change that the deploy command will make (or would make in dry-run).</summary>
public sealed record DeployWorkItemChange(
    int WorkItemId,
    string WorkItemType,
    string Title,
    string CurrentState,
    string NewState,
    string? CurrentAssignee,
    string NewAssignee);
