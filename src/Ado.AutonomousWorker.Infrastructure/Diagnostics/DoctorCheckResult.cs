namespace Ado.AutonomousWorker.Infrastructure.Diagnostics;

public sealed record DoctorCheckResult(string Name, bool Success, string Detail);
