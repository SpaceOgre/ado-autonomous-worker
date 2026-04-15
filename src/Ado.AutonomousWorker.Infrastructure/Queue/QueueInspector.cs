using Ado.AutonomousWorker.Core.Abstractions;

namespace Ado.AutonomousWorker.Infrastructure.Queue;

public sealed class QueueInspector(IAzureDevOpsClient azureDevOpsClient, IRulesEngine rulesEngine)
{
    public async Task<IReadOnlyList<Ado.AutonomousWorker.Core.Models.QueueItem>> GetQueueAsync(CancellationToken cancellationToken)
    {
        var aiWork = await azureDevOpsClient.QueryAiWorkAsync(cancellationToken);
        var aiRework = await azureDevOpsClient.QueryAiReworkAsync(cancellationToken);
        var aiPrFix = await azureDevOpsClient.QueryAiPrFixAsync(cancellationToken);
        return rulesEngine.OrderQueue(aiWork, aiRework, aiPrFix);
    }
}
