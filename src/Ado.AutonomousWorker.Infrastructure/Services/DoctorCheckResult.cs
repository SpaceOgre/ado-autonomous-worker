namespace Ado.AutonomousWorker.Infrastructure.Services;

public sealed record DoctorCheckResult(string Name, bool Success, string Detail);
