namespace Ado.AutonomousWorker.Core.Models;

public sealed record CommandResult(
    string Command,
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool Success => ExitCode == 0;
}
