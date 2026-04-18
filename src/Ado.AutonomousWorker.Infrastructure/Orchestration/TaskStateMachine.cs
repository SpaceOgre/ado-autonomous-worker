using Ado.AutonomousWorker.Core.Enums;
using Microsoft.Extensions.Logging;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

internal sealed class TaskStateMachine(
    IEnumerable<IPhaseHandler> handlers,
    ExecutionJournal journal,
    ILogger<TaskStateMachine> logger
)
{
    private readonly ILookup<TaskPhase, IPhaseHandler> _handlers = handlers.ToLookup(h => h.Phase);

    private IPhaseHandler Resolve(TaskPhase phase, PhaseContext context)
    {
        var candidates = _handlers[phase].Where(h => h.Accepts(context)).ToList();
        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException(
                $"No handler accepts context type '{context.GetType().Name}' for phase {phase}."
            ),
            _ => throw new InvalidOperationException(
                $"Multiple handlers accept context type '{context.GetType().Name}' for phase {phase}."
            ),
        };
    }

    public async Task RunAsync(PhaseContext context, TaskPhase startPhase, CancellationToken cancellationToken)
    {
        var phase = startPhase;

        while (true)
        {
            var handler = Resolve(phase, context);

            logger.LogInformation("Execution {ExecutionId}: executing phase {Phase}.", context.ExecutionId, phase);

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
                logger.LogError(ex, "Execution {ExecutionId}: phase {Phase} failed.", context.ExecutionId, phase);
                await journal.TerminateAsync(
                    context.ExecutionId,
                    ExecutionStatus.Failed,
                    phase,
                    ex.Message,
                    cancellationToken
                );
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
                await journal.TerminateAsync(
                    context.ExecutionId,
                    transition.TerminalStatus!.Value,
                    phase,
                    terminalMessage,
                    cancellationToken
                );
                return;
            }

            await journal.UpdatePhaseAsync(
                context.ExecutionId,
                transition.NextPhase,
                $"{phase} → {transition.NextPhase}",
                cancellationToken
            );

            phase = transition.NextPhase;
        }
    }
}
