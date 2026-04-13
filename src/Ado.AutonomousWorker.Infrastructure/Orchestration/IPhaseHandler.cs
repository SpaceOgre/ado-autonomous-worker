using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

internal interface IPhaseHandler
{
    TaskPhase Phase { get; }
    Task<TaskPhase> ExecuteAsync(PhaseContext context, CancellationToken cancellationToken);
}
