using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Models;

public sealed record AgentSessionHandle(
    string SessionId,
    AgentProvider Provider,
    AgentRole Role,
    string WorkingDirectory,
    string TranscriptPath,
    string? ProviderSessionId = null);
