using Ado.AutonomousWorker.Core.Enums;
using Spectre.Console;

namespace Ado.AutonomousWorker.Cli.Presentation;

public static class CliStyle
{
    public static string Phase(TaskPhase phase)
    {
        var color = phase switch
        {
            TaskPhase.Validating => "aqua",
            TaskPhase.Reviewing => "mediumspringgreen",
            TaskPhase.ApplyingReview => "yellow",
            TaskPhase.Coding => "deepskyblue1",
            TaskPhase.Planning => "cornflowerblue",
            TaskPhase.Publishing => "plum2",
            TaskPhase.Queued => "grey",
            _ => "white",
        };

        return $"[{color}]{Markup.Escape(phase.ToString())}[/]";
    }

    public static string Status(ExecutionStatus status)
    {
        var color = status switch
        {
            ExecutionStatus.Completed => "green",
            ExecutionStatus.Failed => "red",
            ExecutionStatus.Skipped => "yellow",
            ExecutionStatus.Running => "deepskyblue1",
            _ => "white",
        };

        return $"[{color}]{Markup.Escape(status.ToString())}[/]";
    }

    public static string WorkItemState(string state)
    {
        var color = state.ToLowerInvariant() switch
        {
            "new" => "cornflowerblue",
            "active" => "deepskyblue1",
            "resolved" => "yellow",
            "closed" => "green",
            "removed" => "grey",
            "done" => "green",
            "failed" => "red",
            _ => "white",
        };

        return $"[{color}]{Markup.Escape(state)}[/]";
    }

    public static string QueueKind(QueueItemKind kind)
    {
        var color = kind switch
        {
            QueueItemKind.AiRework => "orange1",
            QueueItemKind.AiWork => "aqua",
            _ => "white",
        };

        return $"[{color}]{Markup.Escape(kind.ToString())}[/]";
    }

    public static string WorkItemTypeLabel(WorkItemType type)
    {
        var color = type switch
        {
            WorkItemType.Bug => "red",
            WorkItemType.Task => "yellow",
            WorkItemType.UserStory => "deepskyblue1",
            _ => "white",
        };

        return $"[{color}]{Markup.Escape(type.ToString())}[/]";
    }

    public static string DoctorStatus(bool success)
    {
        return success ? "[green]OK[/]" : "[red]Fail[/]";
    }

    public static string Priority(int? priority)
    {
        return priority switch
        {
            1 => "[red]P1[/]",
            2 => "[yellow]P2[/]",
            3 => "[green]P3[/]",
            4 => "[grey]P4[/]",
            _ => "[grey]-[/]",
        };
    }

    public static string StackRank(double stackRank)
    {
        return stackRank >= double.MaxValue / 2
            ? "[grey]-[/]"
            : stackRank.ToString("0.##");
    }

    public static string Provider(AgentProvider provider)
    {
        var color = provider switch
        {
            AgentProvider.Claude => "mediumpurple2",
            AgentProvider.Codex => "aqua",
            _ => "white",
        };

        return $"[{color}]{Markup.Escape(provider.ToString())}[/]";
    }

    public static Rule SectionRule(string title, string color)
        => new Rule($"[{color}]{Markup.Escape(title)}[/]").RuleStyle(color);

    public static string Role(AgentRole role)
    {
        var color = role switch
        {
            AgentRole.Plan => "cornflowerblue",
            AgentRole.Code => "deepskyblue1",
            AgentRole.Review => "mediumspringgreen",
            _ => "white",
        };

        return $"[{color}]{Markup.Escape(role.ToString())}[/]";
    }
}
