using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Models;

public sealed record PlanResult(
    WorkItemScope Scope,
    IReadOnlyList<string> Repositories,
    IReadOnlyList<string> ValidationTargets,
    IReadOnlyList<string> TestIntent,
    IReadOnlyList<string> Risks,
    string Summary);
