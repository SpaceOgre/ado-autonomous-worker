namespace Ado.AutonomousWorker.Core.Enums;

public enum DeployPhase
{
    Initializing = 0,
    TriggeringPipelines = 1,
    WaitingForPipelines = 2,
    ComputingChanges = 3,
    ApplyingChanges = 4,
    Done = 5,
}
