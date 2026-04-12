namespace Ado.AutonomousWorker.Core.Configuration;

public sealed class StorageOptions
{
    public string DataRoot { get; set; } = ".data";

    public string DatabasePath { get; set; } = ".data\\worker.db";

    public string LogRoot { get; set; } = ".data\\logs";

    public string ArtifactRoot { get; set; } = ".data\\artifacts";

    public string GetWorkItemArtifactRoot(int workItemId)
    {
        return Path.Combine(ArtifactRoot, workItemId.ToString());
    }

    public string GetExecutionArtifactRoot(int workItemId, string executionId)
    {
        return Path.Combine(GetWorkItemArtifactRoot(workItemId), "executions", executionId);
    }

    public string GetTaskArtifactRoot(int workItemId, int childTaskId)
    {
        return Path.Combine(GetWorkItemArtifactRoot(workItemId), "tasks", childTaskId.ToString());
    }

    public void NormalizePaths()
    {
        var defaultRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ado.AutonomousWorker");

        DataRoot = NormalizeRoot(DataRoot, defaultRoot);
        DatabasePath = NormalizeChildPath(DatabasePath, "worker.db", DataRoot);
        LogRoot = NormalizeChildPath(LogRoot, "logs", DataRoot);
        ArtifactRoot = NormalizeChildPath(ArtifactRoot, "artifacts", DataRoot);
    }

    private static string NormalizeRoot(string? value, string defaultRoot)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, ".data", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(defaultRoot);
        }

        return Path.IsPathRooted(value)
            ? Path.GetFullPath(value)
            : Path.GetFullPath(Path.Combine(defaultRoot, value));
    }

    private static string NormalizeChildPath(string? value, string defaultRelativePath, string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Path.GetFullPath(Path.Combine(dataRoot, defaultRelativePath));
        }

        if (Path.IsPathRooted(value))
        {
            return Path.GetFullPath(value);
        }

        const string legacyPrefix = ".data";
        if (string.Equals(value, legacyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(dataRoot);
        }

        if (value.StartsWith($"{legacyPrefix}\\", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith($"{legacyPrefix}/", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(Path.Combine(dataRoot, value[($"{legacyPrefix}\\").Length..]));
        }

        return Path.GetFullPath(Path.Combine(dataRoot, value));
    }
}
