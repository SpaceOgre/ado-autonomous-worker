using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal sealed class PrFixEligibilityHandler(
    IAzureDevOpsClient azureDevOpsClient,
    IOptions<AzureDevOpsOptions> azureDevOpsOptions,
    ILogger<PrFixEligibilityHandler> logger
) : PrFixHandler
{
    public override TaskPhase Phase => TaskPhase.Queued;

    protected override async Task<PhaseTransition> ExecuteAsync(
        PhaseContext.PrFixContext context,
        CancellationToken cancellationToken
    )
    {
        var workItem = context.WorkItem;
        var prTarget = context.PrTarget;

        var currentSprint =
            await azureDevOpsClient.GetCurrentSprintPathAsync(cancellationToken) ?? workItem.IterationPath;
        await EnsureWorkItemReadyAsync(workItem, cancellationToken);

        var threads = await azureDevOpsClient.GetPullRequestThreadsAsync(
            prTarget.RepositoryName,
            prTarget.PullRequestId,
            cancellationToken
        );
        context.PrThreads = threads.Where(t => t.Status is "active" or "pending" && t.Comments.Count > 0).ToList();

        await context.Journal.AppendEventAsync(
            context.ExecutionId,
            TaskPhase.Queued,
            $"PR fix: loaded {context.PrThreads.Count} active review thread(s) from PR #{prTarget.PullRequestId}.",
            cancellationToken
        );
        return PhaseTransition.To(TaskPhase.Coding);
    }

    private async Task EnsureWorkItemReadyAsync(
        Core.Models.WorkItemDetails workItem,
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
    }
}
