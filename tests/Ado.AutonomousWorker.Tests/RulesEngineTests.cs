using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Core.Rules;

namespace Ado.AutonomousWorker.Tests;

public sealed class RulesEngineTests
{
    private readonly RulesEngine _rulesEngine = new();

    [Fact]
    public void OrderQueue_PrioritizesReworkBeforeAiWork()
    {
        var ordered = _rulesEngine.OrderQueue(
            [new WorkItemSummary(2, WorkItemType.Bug, "AI Work", "New", null, 10)],
            [new WorkItemSummary(1, WorkItemType.UserStory, "Rework", "Active", "daniel.nordstrom@consid.se", 50)]);

        Assert.Collection(
            ordered,
            first =>
            {
                Assert.Equal(QueueItemKind.AiRework, first.Kind);
                Assert.Equal(WorkItemType.UserStory, first.WorkItemType);
            },
            second =>
            {
                Assert.Equal(QueueItemKind.AiWork, second.Kind);
                Assert.Equal(WorkItemType.Bug, second.WorkItemType);
            });
    }

    [Fact]
    public void ClassifyScope_UsesTagsBeforeTextAnalysis()
    {
        var scope = _rulesEngine.ClassifyScope(new WorkItemDetails
        {
            Tags = ["Backend"],
            Description = "Update UI page and styling",
            AcceptanceCriteria = "- style the page",
        });

        Assert.Equal(WorkItemScope.Backend, scope);
    }

    [Fact]
    public void ExceedsAcceptanceCriteriaLimit_CountsBulletLines()
    {
        var workItem = new WorkItemDetails
        {
            AcceptanceCriteria = string.Join(Environment.NewLine, Enumerable.Range(1, 11).Select(index => $"- requirement {index}")),
        };

        var exceeds = _rulesEngine.ExceedsAcceptanceCriteriaLimit(workItem, 10, out var lineCount);

        Assert.True(exceeds);
        Assert.Equal(11, lineCount);
    }

    [Fact]
    public void BuildCompletionTags_RemovesWorkTagsAndAddsDone()
    {
        var result = _rulesEngine.BuildCompletionTags(new WorkItemDetails
        {
            Tags = ["AI-Work", "Backend", "AI-Rework"],
        });

        Assert.Contains("AI-Done", result);
        Assert.DoesNotContain("AI-Work", result);
        Assert.DoesNotContain("AI-Rework", result);
        Assert.Contains("Backend", result);
    }
}
