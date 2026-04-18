namespace Ado.AutonomousWorker.Core.Configuration;

public sealed class RepositoryOptions
{
    public RepositoryProfile Backend { get; set; } = new();

    public RepositoryProfile Frontend { get; set; } = new();
}
