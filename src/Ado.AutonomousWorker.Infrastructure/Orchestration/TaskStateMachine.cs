using Ado.AutonomousWorker.Core.Enums;
using Microsoft.Extensions.Logging;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

internal sealed class TaskStateMachine(
    IEnumerable<IPhaseHandler> handlers,
    ExecutionJournal journal,
    ILogger<TaskStateMachine> logger)
{
    private readonly Dictionary<TaskPhase, IPhaseHandler> _handlers =
        handlers.ToDictionary(h => h.Phase);

    public async Task RunAsync(PhaseContext context, TaskPhase startPhase, CancellationToken cancellationToken)
    {
        var phase = startPhase;

        while (true)
        {
            if (!_handlers.TryGetValue(phase, out var handler))
                throw new InvalidOperationException($"No handler registered for phase {phase}.");

            logger.LogInformation(
                "Execution {ExecutionId}: executing phase {Phase}.",
                context.ExecutionId, phase);

            PhaseTransition transition;
            try
            {
                transition = await handler.ExecuteAsync(context, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Execution {ExecutionId}: phase {Phase} failed.",
                    context.ExecutionId, phase);
                await journal.TerminateAsync(context.ExecutionId, ExecutionStatus.Failed, phase, ex.Message, cancellationToken);
                throw;
            }

            if (transition.IsTerminal)
            {
                var terminalMessage = transition.TerminalStatus switch
                {
                    ExecutionStatus.Completed => "Execution completed successfully.",
                    ExecutionStatus.Skipped => "Execution skipped.",
                    _ => "Execution terminated.",
                };
                await journal.TerminateAsync(context.ExecutionId, transition.TerminalStatus!.Value, phase, terminalMessage, cancellationToken);
                return;
            }

            await journal.UpdatePhaseAsync(
                context.ExecutionId,
                transition.NextPhase,
                $"{phase} → {transition.NextPhase}",
                cancellationToken);

            phase = transition.NextPhase;
        }
    }
}
