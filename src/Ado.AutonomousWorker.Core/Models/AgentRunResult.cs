namespace Ado.AutonomousWorker.Core.Models;

public sealed record AgentRunResult(
    bool Success,
    string Summary,
    string TranscriptPath,
    string? RawOutput = null,
    string? StructuredPayload = null,
    AgentUsage? Usage = null
);
