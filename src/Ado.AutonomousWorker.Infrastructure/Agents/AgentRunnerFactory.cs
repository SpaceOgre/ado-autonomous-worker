using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace Ado.AutonomousWorker.Infrastructure.Agents;

public sealed class AgentRunnerFactory(IServiceProvider serviceProvider) : IAgentRunnerFactory
{
    public IAgentRunner GetRunner(AgentProvider provider)
    {
        return provider switch
        {
            AgentProvider.Codex => serviceProvider.GetRequiredService<CodexRunner>(),
            AgentProvider.Claude => serviceProvider.GetRequiredService<ClaudeRunner>(),
            _ => throw new InvalidOperationException($"Unsupported agent provider '{provider}'."),
        };
    }
}
