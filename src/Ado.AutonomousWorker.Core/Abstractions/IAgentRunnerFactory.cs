using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Abstractions;

public interface IAgentRunnerFactory
{
    IAgentRunner GetRunner(AgentProvider provider);
}
