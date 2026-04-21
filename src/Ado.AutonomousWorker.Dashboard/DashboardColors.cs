using Ado.AutonomousWorker.Core.Enums;
using MudBlazor;

namespace Ado.AutonomousWorker.Dashboard;

public static class DashboardColors
{
    public static Color ForKind(QueueItemKind kind) => kind switch
    {
        QueueItemKind.AiWork => Color.Info,
        QueueItemKind.AiRework => Color.Warning,
        QueueItemKind.AiPrFix => Color.Secondary,
        _ => Color.Default,
    };

    public static Color ForPhase(TaskPhase phase) => phase switch
    {
        TaskPhase.Queued => Color.Default,
        TaskPhase.Planning => Color.Info,
        TaskPhase.Coding => Color.Info,
        TaskPhase.Reviewing => Color.Success,
        TaskPhase.ApplyingReview => Color.Warning,
        TaskPhase.FixingValidation => Color.Warning,
        TaskPhase.Validating => Color.Info,
        TaskPhase.Publishing => Color.Primary,
        _ => Color.Default,
    };

    public static Color ForStatus(ExecutionStatus status) => status switch
    {
        ExecutionStatus.Running => Color.Info,
        ExecutionStatus.Completed => Color.Success,
        ExecutionStatus.Failed => Color.Error,
        ExecutionStatus.Skipped => Color.Default,
        _ => Color.Default,
    };

    public static string FormatKind(QueueItemKind kind) => kind switch
    {
        QueueItemKind.AiWork => "AI-Work",
        QueueItemKind.AiRework => "AI-Rework",
        QueueItemKind.AiPrFix => "AI-PrFix",
        _ => kind.ToString(),
    };

    public static string CacheHitPercent(long inputTokens, long cachedTokens)
    {
        var total = inputTokens + cachedTokens;
        return total == 0 ? "-" : $"{(double)cachedTokens / total * 100:F1}%";
    }

    public static Color ForSeverity(string? severity) =>
        severity?.ToUpperInvariant() switch
        {
            "CRITICAL" or "HIGH" => Color.Error,
            "MEDIUM" => Color.Warning,
            "LOW" or "INFO" => Color.Info,
            _ => Color.Default,
        };

    private static readonly Dictionary<string, int> SeverityOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CRITICAL"] = 0,
        ["HIGH"] = 1,
        ["MEDIUM"] = 2,
        ["LOW"] = 3,
        ["INFO"] = 4,
    };

    public static int SeverityRank(string? severity) =>
        severity is not null && SeverityOrder.TryGetValue(severity, out var rank) ? rank : 99;

    public static string FormatDuration(TimeSpan ts) =>
        ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s"
            : ts.TotalMinutes >= 1
                ? $"{(int)ts.TotalMinutes}m {ts.Seconds}s"
                : $"{ts.Seconds}s";

    public static Color ForDeployStatus(DeployStatus status) => status switch
    {
        DeployStatus.Running => Color.Info,
        DeployStatus.Completed => Color.Success,
        DeployStatus.Failed => Color.Error,
        _ => Color.Default,
    };

    public static Color ForDeployPhase(DeployPhase phase) => phase switch
    {
        DeployPhase.TriggeringPipelines => Color.Info,
        DeployPhase.WaitingForPipelines => Color.Info,
        DeployPhase.ComputingChanges => Color.Warning,
        DeployPhase.ApplyingChanges => Color.Primary,
        DeployPhase.Done => Color.Success,
        _ => Color.Default,
    };

    public static Color ForWorkItemType(string? type) =>
        type?.ToUpperInvariant() switch
        {
            "TASK" => Color.Warning,
            "BUG" => Color.Error,
            "USER STORY" => Color.Info,
            _ => Color.Default,
        };
}
