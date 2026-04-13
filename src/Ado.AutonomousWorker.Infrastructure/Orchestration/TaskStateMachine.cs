using Ado.AutonomousWorker.Core.Enums;
using Microsoft.Extensions.Logging;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

internal sealed class TaskStateMachine(
    IEnumerable<IPhaseHandler> handlers,
    ExecutionJournal journal,
    ILogger<TaskStateMachine> logger)
{
    private static readonly HashSet<TaskPhase> TerminalPhases =
        [TaskPhase.Completed, TaskPhase.Failed, TaskPhase.Skipped];

    private readonly Dictionary<TaskPhase, IPhaseHandler> _handlers =
        handlers.ToDictionary(h => h.Phase);

    public async Task RunAsync(PhaseContext context, TaskPhase startPhase, CancellationToken cancellationToken)
    {
        var phase = startPhase;

        while (!TerminalPhases.Contains(phase))
        {
            if (!_handlers.TryGetValue(phase, out var handler))
                throw new InvalidOperationException($"No handler registered for phase {phase}.");

            logger.LogInformation(
                "Execution {ExecutionId}: executing phase {Phase}.",
                context.ExecutionId, phase);

            var next = await handler.ExecuteAsync(context, cancellationToken);

            if (next != phase)
            {
                await journal.UpdatePhaseAsync(
                    context.ExecutionId,
                    next,
                    $"{phase} → {next}",
                    cancellationToken);
            }

            phase = next;
        }

        logger.LogInformation(
            "Execution {ExecutionId}: reached terminal phase {Phase}.",
            context.ExecutionId, phase);
    }
}
