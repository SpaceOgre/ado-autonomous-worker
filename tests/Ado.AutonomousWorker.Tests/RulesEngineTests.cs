using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Core.Rules;

namespace Ado.AutonomousWorker.Tests;

public sealed class RulesEngineTests
{
    private readonly RulesEngine _rulesEngine = new();

    [Test]
    public async Task OrderQueue_PrioritizesReworkBeforeAiWork()
    {
        var ordered = _rulesEngine
            .OrderQueue(
                [new WorkItemSummary(2, WorkItemType.Bug, "AI Work", "New", null, 10)],
                [new WorkItemSummary(1, WorkItemType.UserStory, "Rework", "Active", "daniel.nordstrom@consid.se", 50)]
            )
            .ToList();

        await Assert.That(ordered.Count).IsEqualTo(2);
        await Assert.That(ordered[0].Kind).IsEqualTo(QueueItemKind.AiRework);
        await Assert.That(ordered[0].WorkItemType).IsEqualTo(WorkItemType.UserStory);
        await Assert.That(ordered[1].Kind).IsEqualTo(QueueItemKind.AiWork);
        await Assert.That(ordered[1].WorkItemType).IsEqualTo(WorkItemType.Bug);
    }

    [Test]
    public async Task ClassifyScope_UsesTagsBeforeTextAnalysis()
    {
        var scope = _rulesEngine.ClassifyScope(
            new WorkItemDetails
            {
                Tags = ["Backend"],
                Description = "Update UI page and styling",
                AcceptanceCriteria = "- style the page",
            }
        );

        await Assert.That(scope).IsEqualTo(WorkItemScope.Backend);
    }

    [Test]
    public async Task ExceedsAcceptanceCriteriaLimit_CountsBulletLines()
    {
        var workItem = new WorkItemDetails
        {
            AcceptanceCriteria = string.Join(
                Environment.NewLine,
                Enumerable.Range(1, 11).Select(index => $"- requirement {index}")
            ),
        };

        var exceeds = _rulesEngine.ExceedsAcceptanceCriteriaLimit(workItem, 10, out var lineCount);

        await Assert.That(exceeds).IsTrue();
        await Assert.That(lineCount).IsEqualTo(11);
    }

    [Test]
    public async Task BuildCompletionTags_RemovesWorkTagsAndAddsDone()
    {
        var result = _rulesEngine.BuildCompletionTags(
            new WorkItemDetails { Tags = ["AI-Work", "Backend", "AI-Rework"] }
        );

        await Assert.That(result).Contains("AI-Done");
        await Assert.That(result).DoesNotContain("AI-Work");
        await Assert.That(result).DoesNotContain("AI-Rework");
        await Assert.That(result).Contains("Backend");
    }
}
