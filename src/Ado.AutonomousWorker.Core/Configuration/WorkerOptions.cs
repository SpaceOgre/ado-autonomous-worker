namespace Ado.AutonomousWorker.Core.Configuration;

public sealed class WorkerOptions
{
    public int MaxItemsPerRun { get; set; } = 5;

    public int ReviewCycleLimit { get; set; } = 1;

    public bool DryRun { get; set; }

    public bool CleanupWorktreesAfterSuccess { get; set; }

    public string CurrentSprintFallback { get; set; } = string.Empty;
}
