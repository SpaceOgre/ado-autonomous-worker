using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal abstract class WorkItemHandler : IPhaseHandler
{
    public abstract TaskPhase Phase { get; }

    public bool Accepts(PhaseContext context) => context is PhaseContext.WorkItemContext;

    public Task<PhaseTransition> ExecuteAsync(PhaseContext context, CancellationToken cancellationToken) =>
        ExecuteAsync((PhaseContext.WorkItemContext)context, cancellationToken);

    protected abstract Task<PhaseTransition> ExecuteAsync(
        PhaseContext.WorkItemContext context,
        CancellationToken cancellationToken
    );
}
