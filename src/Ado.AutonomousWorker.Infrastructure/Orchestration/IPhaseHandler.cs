using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

internal interface IPhaseHandler
{
    TaskPhase Phase { get; }
    bool Accepts(PhaseContext context);
    Task<PhaseTransition> ExecuteAsync(PhaseContext context, CancellationToken cancellationToken);
}
