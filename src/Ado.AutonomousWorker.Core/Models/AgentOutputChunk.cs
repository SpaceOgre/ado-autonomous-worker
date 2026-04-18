using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Models;

public sealed record AgentOutputChunk(
    string SessionId,
    AgentProvider Provider,
    AgentRole Role,
    string WorkingDirectory,
    AgentOutputStream Stream,
    long Sequence,
    DateTimeOffset Timestamp,
    string Content
);
