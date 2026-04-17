using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

internal readonly record struct PhaseTransition
{
    public static PhaseTransition To(TaskPhase phase) => new(phase, null);

    public static PhaseTransition Complete() => new(default, ExecutionStatus.Completed);

    public static PhaseTransition Skip() => new(default, ExecutionStatus.Skipped);

    private PhaseTransition(TaskPhase phase, ExecutionStatus? terminalStatus)
    {
        NextPhase = phase;
        TerminalStatus = terminalStatus;
    }

    public TaskPhase NextPhase { get; }
    public ExecutionStatus? TerminalStatus { get; }
    public bool IsTerminal => TerminalStatus.HasValue;
}
