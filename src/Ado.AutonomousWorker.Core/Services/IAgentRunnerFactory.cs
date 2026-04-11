using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Services;

public interface IAgentRunnerFactory
{
    IAgentRunner GetRunner(AgentProvider provider);
}
