using Ado.AutonomousWorker.Core.Services;

namespace Ado.AutonomousWorker.Infrastructure.Services;

public sealed class QueueInspector(IAzureDevOpsClient azureDevOpsClient, IRulesEngine rulesEngine)
{
    public async Task<IReadOnlyList<Ado.AutonomousWorker.Core.Models.QueueItem>> GetQueueAsync(CancellationToken cancellationToken)
    {
        var aiWork = await azureDevOpsClient.QueryAiWorkAsync(cancellationToken);
        var aiRework = await azureDevOpsClient.QueryAiReworkAsync(cancellationToken);
        return rulesEngine.OrderQueue(aiWork, aiRework);
    }
}
