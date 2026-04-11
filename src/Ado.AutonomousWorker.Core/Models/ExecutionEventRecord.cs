using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Models;

public sealed record ExecutionEventRecord(
    string ExecutionId,
    DateTimeOffset Timestamp,
    TaskPhase Phase,
    string Message);
