using Ado.AutonomousWorker.Cli.Presentation;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Tests;

public sealed class TerminalTitleFormatterTests
{
    [Test]
    public async Task BuildWaitingTitle_IncludesSpinnerAndBaseTitle()
    {
        var title = TerminalTitleFormatter.BuildWaitingTitle("Queue 1/3 - Work item #28639", 0, useUnicode: false);

        await Assert.That(title).Contains("Queue 1/3 - Work item #28639");
        await Assert.That(title).Contains("Waiting");
        await Assert.That(title).StartsWith("-");
    }

    [Test]
    public async Task BuildExecutionTitle_UsesPhaseForActiveExecution()
    {
        var execution = new TaskExecutionRecord(
            "exec-123",
            28639,
            "Title",
            QueueItemKind.AiWork,
            TaskPhase.Planning,
            ExecutionStatus.Running,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow
        );

        var title = TerminalTitleFormatter.BuildExecutionTitle(
            "Queue 1/3 - Work item #28639",
            execution,
            2,
            useUnicode: true
        );

        await Assert.That(title).Contains("Planning");
        await Assert.That(title).Contains("Queue 1/3 - Work item #28639");
        await Assert.That(title).Contains("ado-worker");
    }

    [Test]
    public async Task BuildExecutionTitle_UsesCompletionMarkerForCompletedExecution()
    {
        var execution = new TaskExecutionRecord(
            "exec-123",
            28639,
            "Title",
            QueueItemKind.AiWork,
            TaskPhase.Publishing,
            ExecutionStatus.Completed,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow
        );

        var title = TerminalTitleFormatter.BuildExecutionTitle("Work item #28639", execution, 0, useUnicode: true);

        await Assert.That(title).StartsWith("✓");
        await Assert.That(title).Contains("Completed");
    }
}
