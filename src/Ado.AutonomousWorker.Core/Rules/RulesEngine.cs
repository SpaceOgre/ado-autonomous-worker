using System.Text.RegularExpressions;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Core.Rules;

public sealed partial class RulesEngine : IRulesEngine
{
    private static readonly string[] BackendKeywords = ["api", "database", "service", "migration", "endpoint"];
    private static readonly string[] FrontendKeywords = ["ui", "component", "page", "styling", "screen"];

    public IReadOnlyList<QueueItem> OrderQueue(IReadOnlyList<WorkItemSummary> aiWorkItems, IReadOnlyList<WorkItemSummary> aiReworkItems)
    {
        var ordered = aiReworkItems
            .OrderBy(item => item.StackRank)
            .ThenBy(item => item.Priority ?? int.MaxValue)
            .Select(item => new QueueItem(item.WorkItemId, item.WorkItemType, item.Title, QueueItemKind.AiRework, item.StackRank, item.Priority, item.State, item.AssignedTo))
            .Concat(
                aiWorkItems
                    .OrderBy(item => item.StackRank)
                    .ThenBy(item => item.Priority ?? int.MaxValue)
                    .Select(item => new QueueItem(item.WorkItemId, item.WorkItemType, item.Title, QueueItemKind.AiWork, item.StackRank, item.Priority, item.State, item.AssignedTo)))
            .GroupBy(item => item.WorkItemId)
            .Select(group => group.First())
            .ToList();

        return ordered;
    }

    public bool IsEligible(WorkItemDetails workItem, string defaultAssignee)
    {
        var tags = workItem.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assigneeMatches = string.IsNullOrWhiteSpace(workItem.AssignedTo) ||
                              string.Equals(workItem.AssignedTo, defaultAssignee, StringComparison.OrdinalIgnoreCase);

        return workItem.Kind switch
        {
            QueueItemKind.AiWork => tags.Contains("AI-Work") &&
                                    !tags.Contains("AI-Failed") &&
                                    string.Equals(workItem.State, "New", StringComparison.OrdinalIgnoreCase) &&
                                    assigneeMatches,
            QueueItemKind.AiRework => tags.Contains("AI-Rework") &&
                                      !tags.Contains("AI-Failed") &&
                                      !string.Equals(workItem.State, "Closed", StringComparison.OrdinalIgnoreCase) &&
                                      !string.Equals(workItem.State, "Removed", StringComparison.OrdinalIgnoreCase) &&
                                      string.Equals(workItem.AssignedTo, defaultAssignee, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    public WorkItemScope ClassifyScope(WorkItemDetails workItem)
    {
        var tags = workItem.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasBackendTag = tags.Contains("Backend");
        var hasFrontendTag = tags.Contains("Frontend");

        if (hasBackendTag && !hasFrontendTag)
        {
            return WorkItemScope.Backend;
        }

        if (hasFrontendTag && !hasBackendTag)
        {
            return WorkItemScope.Frontend;
        }

        if (hasBackendTag && hasFrontendTag)
        {
            return WorkItemScope.Fullstack;
        }

        var combinedText = $"{workItem.Description}\n{workItem.AcceptanceCriteria}";
        var lowered = combinedText.ToLowerInvariant();
        var backendMatches = BackendKeywords.Any(lowered.Contains);
        var frontendMatches = FrontendKeywords.Any(lowered.Contains);

        return (backendMatches, frontendMatches) switch
        {
            (true, false) => WorkItemScope.Backend,
            (false, true) => WorkItemScope.Frontend,
            (true, true) => WorkItemScope.Fullstack,
            _ => WorkItemScope.Fullstack,
        };
    }

    public bool ExceedsAcceptanceCriteriaLimit(WorkItemDetails workItem, int maxLines, out int lineCount)
    {
        lineCount = AcceptanceCriteriaLineRegex()
            .Matches(workItem.AcceptanceCriteria ?? string.Empty)
            .Count;

        return lineCount > maxLines;
    }

    public IReadOnlyCollection<string> BuildCompletionTags(WorkItemDetails workItem)
    {
        var tags = workItem.Tags
            .Where(tag => !string.Equals(tag, "AI-Work", StringComparison.OrdinalIgnoreCase) &&
                          !string.Equals(tag, "AI-Rework", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        tags.Add("AI-Done");
        return tags.OrderBy(tag => tag).ToArray();
    }

    public IReadOnlyCollection<string> BuildFailureTags(WorkItemDetails workItem)
    {
        var tags = workItem.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        tags.Add("AI-Failed");
        return tags.OrderBy(tag => tag).ToArray();
    }

    [GeneratedRegex(@"^\s*([-*]|\d+\.)\s+", RegexOptions.Multiline)]
    private static partial Regex AcceptanceCriteriaLineRegex();
}
