using Ado.AutonomousWorker.Cli.Presentation;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Tests;

public sealed class TerminalTitleFormatterTests
{
    [Fact]
    public void BuildWaitingTitle_IncludesSpinnerAndBaseTitle()
    {
        var title = TerminalTitleFormatter.BuildWaitingTitle("Queue 1/3 - Work item #28639", 0, useUnicode: false);

        Assert.Contains("Queue 1/3 - Work item #28639", title);
        Assert.Contains("Waiting", title);
        Assert.StartsWith("-", title);
    }

    [Fact]
    public void BuildExecutionTitle_UsesPhaseForActiveExecution()
    {
        var execution = new TaskExecutionRecord(
            "exec-123",
            28639,
            "Title",
            QueueItemKind.AiWork,
            TaskPhase.Planning,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var title = TerminalTitleFormatter.BuildExecutionTitle("Queue 1/3 - Work item #28639", execution, 2, useUnicode: true);

        Assert.Contains("Planning", title);
        Assert.Contains("Queue 1/3 - Work item #28639", title);
        Assert.Contains("ado-worker", title);
    }

    [Fact]
    public void BuildExecutionTitle_UsesCompletionMarkerForCompletedExecution()
    {
        var execution = new TaskExecutionRecord(
            "exec-123",
            28639,
            "Title",
            QueueItemKind.AiWork,
            TaskPhase.Completed,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var title = TerminalTitleFormatter.BuildExecutionTitle("Work item #28639", execution, 0, useUnicode: true);

        Assert.StartsWith("✓", title);
        Assert.Contains("Completed", title);
    }
}
