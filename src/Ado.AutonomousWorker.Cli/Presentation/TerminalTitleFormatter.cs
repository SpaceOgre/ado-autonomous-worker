using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Cli.Presentation;

public static class TerminalTitleFormatter
{
    private static readonly string[] UnicodeSpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly string[] AsciiSpinnerFrames = ["-", "\\", "|", "/"];

    public static string BuildWaitingTitle(string baseTitle, int tick, bool useUnicode)
    {
        return $"{GetSpinnerFrame(tick, useUnicode)} {baseTitle} | Waiting | ado-worker";
    }

    public static string BuildExecutionTitle(
        string baseTitle,
        TaskExecutionRecord? execution,
        int tick,
        bool useUnicode
    )
    {
        if (execution is null)
            return BuildWaitingTitle(baseTitle, tick, useUnicode);

        var (phaseText, prefix) = execution.Status switch
        {
            ExecutionStatus.Completed => ("Completed", useUnicode ? "✓" : "*"),
            ExecutionStatus.Failed => ($"Failed ({execution.Phase})", useUnicode ? "✗" : "!"),
            ExecutionStatus.Skipped => ("Skipped", useUnicode ? "↷" : "~"),
            _ => (execution.Phase.ToString(), GetSpinnerFrame(tick, useUnicode)),
        };

        return $"{prefix} {baseTitle} | {phaseText} | ado-worker";
    }

    private static string GetSpinnerFrame(int tick, bool useUnicode)
    {
        var frames = useUnicode ? UnicodeSpinnerFrames : AsciiSpinnerFrames;
        return frames[Math.Abs(tick) % frames.Length];
    }
}
