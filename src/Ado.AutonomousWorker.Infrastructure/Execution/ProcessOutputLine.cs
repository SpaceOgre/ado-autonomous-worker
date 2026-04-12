using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Infrastructure.Execution;

public sealed record ProcessOutputLine(
    AgentOutputStream Stream,
    string Content);
