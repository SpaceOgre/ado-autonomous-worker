namespace Ado.AutonomousWorker.Core.Configuration;

public sealed class StorageOptions
{
    /// <summary>The default %LOCALAPPDATA%\Ado.AutonomousWorker directory used for data and user config.</summary>
    public static string DefaultAppFolder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ado.AutonomousWorker");

    public string DataRoot { get; set; } = string.Empty;

    public string DatabasePath { get; set; } = string.Empty;

    public string LogRoot { get; set; } = string.Empty;

    public string ArtifactRoot { get; set; } = string.Empty;

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

    /// <summary>
    /// Returns a .data path under the repository root for development runs,
    /// found by walking up from the binary output directory until a .git marker is located.
    /// Returns null in non-development environments.
    /// </summary>
    public static string? GetDevStorageOverride(bool isDevelopment)
    {
        if (!isDevelopment)
            return null;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetDirectories(".git").Length > 0)
                return Path.Combine(dir.FullName, ".data");
            dir = dir.Parent;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), ".data");
    }

    public void NormalizePaths(string? baseOverride = null)
    {
        var defaultRoot = string.IsNullOrWhiteSpace(baseOverride) ? DefaultAppFolder : baseOverride;
        DataRoot = NormalizeRoot(DataRoot, defaultRoot);
        DatabasePath = NormalizeChildPath(DatabasePath, "worker.db", DataRoot);
        LogRoot = NormalizeChildPath(LogRoot, "logs", DataRoot);
        ArtifactRoot = NormalizeChildPath(ArtifactRoot, "artifacts", DataRoot);
    }

    private static string NormalizeRoot(string? value, string defaultRoot)
    {
        if (string.IsNullOrWhiteSpace(value))
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

        return Path.IsPathRooted(value)
            ? Path.GetFullPath(value)
            : Path.GetFullPath(Path.Combine(dataRoot, value));
    }
}
