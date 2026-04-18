namespace Ado.AutonomousWorker.Core.Models;

public sealed record AgentSessionStartResult(AgentSessionHandle Session, AgentRunResult Result);
