namespace Ado.AutonomousWorker.Core.Models;

public sealed record DeployEventRecord(int Id, string DeployId, DateTimeOffset Timestamp, string Message);
