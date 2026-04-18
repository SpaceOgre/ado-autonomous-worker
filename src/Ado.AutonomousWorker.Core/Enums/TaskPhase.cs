namespace Ado.AutonomousWorker.Core.Enums;

public enum TaskPhase
{
    Queued = 0,
    Planning = 1,
    Coding = 2,
    Reviewing = 3,
    ApplyingReview = 4,
    Validating = 5,
    Publishing = 6,
}
