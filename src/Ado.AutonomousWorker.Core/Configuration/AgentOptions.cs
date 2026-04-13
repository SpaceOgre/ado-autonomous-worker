namespace Ado.AutonomousWorker.Core.Configuration;

public sealed class AgentOptions
{
    public AgentRoleConfig Plan { get; set; } = new();

    public AgentRoleConfig Code { get; set; } = new();

    public AgentRoleConfig Review { get; set; } = new();

    public AgentRoleConfig Pr { get; set; } = new();
}
