using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal sealed class WorkItemEligibilityHandler(
    IAzureDevOpsClient azureDevOpsClient,
    IRulesEngine rulesEngine,
    IOptions<AzureDevOpsOptions> azureDevOpsOptions,
    ILogger<WorkItemEligibilityHandler> logger
) : WorkItemHandler
{
    public override TaskPhase Phase => TaskPhase.Queued;

    protected override async Task<PhaseTransition> ExecuteAsync(
        PhaseContext.WorkItemContext context,
        CancellationToken cancellationToken
    )
    {
        var workItem = context.WorkItem;

        if (!context.IsExplicitResume && !rulesEngine.IsEligible(workItem, azureDevOpsOptions.Value.DefaultAssignee))
        {
            await context.Journal.AppendEventAsync(
                context.ExecutionId,
                TaskPhase.Queued,
                "Work item is no longer eligible for automation.",
                cancellationToken
            );
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
                cancellationToken
            );
            await context.Journal.AppendEventAsync(
                context.ExecutionId,
                TaskPhase.Queued,
                "Acceptance criteria exceeded the configured limit.",
                cancellationToken
            );
            return PhaseTransition.Skip();
        }

        var currentSprint =
            context.IterationOverride ?? await azureDevOpsClient.GetCurrentSprintPathAsync(cancellationToken);

        // Backlog items have a path that is a strict ancestor of the sprint path
        // (e.g. "PRIIS\03 Basuppföljning" is a parent of "PRIIS\03 Basuppföljning\Sprint 39").
        // Items already in any sprint (current, past, or future) have a path that is not
        // a strict ancestor, so they are left in their assigned sprint.
        var isInBacklog = currentSprint is not null
            ? currentSprint.StartsWith(workItem.IterationPath + '\\', StringComparison.OrdinalIgnoreCase)
            : !workItem.IterationPath.Contains('\\');

        context.CurrentSprintPath = isInBacklog ? currentSprint ?? workItem.IterationPath : workItem.IterationPath;

        await EnsureWorkItemReadyAsync(workItem, context.CurrentSprintPath, cancellationToken);
        return PhaseTransition.To(TaskPhase.Planning);
    }

    private async Task EnsureWorkItemReadyAsync(
        Core.Models.WorkItemDetails workItem,
        string currentSprint,
        CancellationToken cancellationToken
    )
    {
        if (!string.Equals(workItem.State, "Active", StringComparison.OrdinalIgnoreCase))
        {
            await azureDevOpsClient.UpdateWorkItemStateAsync(workItem.WorkItemId, "Active", cancellationToken);
            logger.LogInformation("Activated work item #{WorkItemId}.", workItem.WorkItemId);
        }

        if (string.IsNullOrWhiteSpace(workItem.AssignedTo))
        {
            await azureDevOpsClient.AssignWorkItemAsync(
                workItem.WorkItemId,
                azureDevOpsOptions.Value.DefaultAssignee,
                cancellationToken
            );
            logger.LogInformation(
                "Assigned work item #{WorkItemId} to {Assignee}.",
                workItem.WorkItemId,
                azureDevOpsOptions.Value.DefaultAssignee
            );
        }

        if (
            workItem.Kind == QueueItemKind.AiWork
            && !string.IsNullOrWhiteSpace(currentSprint)
            && !string.Equals(workItem.IterationPath, currentSprint, StringComparison.OrdinalIgnoreCase)
        )
        {
            await azureDevOpsClient.UpdateIterationPathAsync(workItem.WorkItemId, currentSprint, cancellationToken);
            logger.LogInformation(
                "Set iteration for #{WorkItemId} to '{IterationPath}'.",
                workItem.WorkItemId,
                currentSprint
            );
        }
    }
}
