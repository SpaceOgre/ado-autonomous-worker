using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal sealed class EligibilityHandler(
    IAzureDevOpsClient azureDevOpsClient,
    IRulesEngine rulesEngine,
    IOptions<AzureDevOpsOptions> azureDevOpsOptions,
    ILogger<EligibilityHandler> logger) : IPhaseHandler
{
    public TaskPhase Phase => TaskPhase.Queued;

    public async Task<PhaseTransition> ExecuteAsync(PhaseContext context, CancellationToken cancellationToken)
    {
        var workItem = context.WorkItem;

        if (!context.IsExplicitResume &&
            !rulesEngine.IsEligible(workItem, azureDevOpsOptions.Value.DefaultAssignee))
        {
            await context.Journal.AppendEventAsync(
                context.ExecutionId,
                TaskPhase.Queued,
                "Work item is no longer eligible for automation.",
                cancellationToken);
            return PhaseTransition.Skip();
        }

        if (rulesEngine.ExceedsAcceptanceCriteriaLimit(workItem, 10, out var lineCount))
        {
            var tags = workItem.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            tags.Add("AI-TooLarge");
            await azureDevOpsClient.UpdateTagsAsync(workItem.WorkItemId, tags.ToArray(), cancellationToken);
            await azureDevOpsClient.AddCommentAsync(
                workItem.WorkItemId,
                $"Skipped automatically because acceptance criteria contained {lineCount} requirement lines.",
                cancellationToken);
            await context.Journal.AppendEventAsync(
                context.ExecutionId,
                TaskPhase.Queued,
                "Acceptance criteria exceeded the configured limit.",
                cancellationToken);
            return PhaseTransition.Skip();
        }

        context.CurrentSprintPath =
            await azureDevOpsClient.GetCurrentSprintPathAsync(cancellationToken) ?? workItem.IterationPath;

        await EnsureWorkItemReadyAsync(workItem, context.CurrentSprintPath, cancellationToken);
        return PhaseTransition.To(TaskPhase.Planning);
    }

    private async Task EnsureWorkItemReadyAsync(
        Core.Models.WorkItemDetails workItem,
        string currentSprint,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(workItem.State, "Active", StringComparison.OrdinalIgnoreCase))
        {
            await azureDevOpsClient.UpdateWorkItemStateAsync(workItem.WorkItemId, "Active", cancellationToken);
            logger.LogInformation("Activated work item #{WorkItemId}.", workItem.WorkItemId);
        }

        if (string.IsNullOrWhiteSpace(workItem.AssignedTo))
        {
            await azureDevOpsClient.AssignWorkItemAsync(
                workItem.WorkItemId, azureDevOpsOptions.Value.DefaultAssignee, cancellationToken);
            logger.LogInformation(
                "Assigned work item #{WorkItemId} to {Assignee}.",
                workItem.WorkItemId, azureDevOpsOptions.Value.DefaultAssignee);
        }

        if (workItem.Kind == QueueItemKind.AiWork && !string.IsNullOrWhiteSpace(currentSprint))
        {
            await azureDevOpsClient.UpdateIterationPathAsync(workItem.WorkItemId, currentSprint, cancellationToken);
            logger.LogInformation(
                "Set iteration for #{WorkItemId} to '{IterationPath}'.",
                workItem.WorkItemId, currentSprint);
        }
    }
}
