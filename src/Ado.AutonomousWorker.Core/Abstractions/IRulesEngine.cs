using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Core.Abstractions;

public interface IRulesEngine
{
    IReadOnlyList<QueueItem> OrderQueue(
        IReadOnlyList<WorkItemSummary> aiWorkItems,
        IReadOnlyList<WorkItemSummary> aiReworkItems,
        IReadOnlyList<QueueItem>? aiPrFixItems = null
    );

    bool IsEligible(WorkItemDetails workItem, string defaultAssignee);

    WorkItemScope ClassifyScope(WorkItemDetails workItem);

    bool ExceedsAcceptanceCriteriaLimit(WorkItemDetails workItem, int maxLines, out int lineCount);

    IReadOnlyCollection<string> BuildCompletionTags(WorkItemDetails workItem);

    IReadOnlyCollection<string> BuildFailureTags(WorkItemDetails workItem);
}
