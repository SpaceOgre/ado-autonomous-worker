using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal abstract class PrFixHandler : IPhaseHandler
{
    public abstract TaskPhase Phase { get; }

    public bool Accepts(PhaseContext context) => context is PhaseContext.PrFixContext;

    public Task<PhaseTransition> ExecuteAsync(PhaseContext context, CancellationToken cancellationToken) =>
        ExecuteAsync((PhaseContext.PrFixContext)context, cancellationToken);

    protected abstract Task<PhaseTransition> ExecuteAsync(
        PhaseContext.PrFixContext context,
        CancellationToken cancellationToken
    );
}
