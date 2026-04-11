namespace Ado.AutonomousWorker.Core.Configuration;

public sealed class StorageOptions
{
    public string DataRoot { get; set; } = ".data";

    public string DatabasePath { get; set; } = ".data\\worker.db";

    public string LogRoot { get; set; } = ".data\\logs";

    public string ArtifactRoot { get; set; } = ".data\\artifacts";
}
