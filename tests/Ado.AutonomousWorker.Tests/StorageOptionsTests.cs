using Ado.AutonomousWorker.Core.Configuration;

namespace Ado.AutonomousWorker.Tests;

public sealed class StorageOptionsTests
{
    [Fact]
    public void NormalizePaths_DefaultsToLocalAppData()
    {
        var options = new StorageOptions();

        options.NormalizePaths();

        var expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ado.AutonomousWorker");

        Assert.Equal(Path.GetFullPath(expectedRoot), options.DataRoot);
        Assert.Equal(Path.Combine(Path.GetFullPath(expectedRoot), "worker.db"), options.DatabasePath);
        Assert.Equal(Path.Combine(Path.GetFullPath(expectedRoot), "logs"), options.LogRoot);
        Assert.Equal(Path.Combine(Path.GetFullPath(expectedRoot), "artifacts"), options.ArtifactRoot);
    }

    [Fact]
    public void NormalizePaths_WithBaseOverride_ResolvesRelativeDefaultsUnderOverride()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "ado-worker-dev-base");
        var options = new StorageOptions();

        options.NormalizePaths(baseOverride: baseDir);

        Assert.Equal(Path.GetFullPath(baseDir), options.DataRoot);
        Assert.Equal(Path.Combine(Path.GetFullPath(baseDir), "worker.db"), options.DatabasePath);
        Assert.Equal(Path.Combine(Path.GetFullPath(baseDir), "logs"), options.LogRoot);
        Assert.Equal(Path.Combine(Path.GetFullPath(baseDir), "artifacts"), options.ArtifactRoot);
    }

    [Fact]
    public void NormalizePaths_KeepsAbsoluteOverrides()
    {
        var root = Path.Combine(Path.GetTempPath(), "ado-worker-storage-test");
        var options = new StorageOptions
        {
            DataRoot = root,
            DatabasePath = Path.Combine(root, "custom.db"),
            LogRoot = Path.Combine(root, "custom-logs"),
            ArtifactRoot = Path.Combine(root, "custom-artifacts"),
        };

        options.NormalizePaths();

        Assert.Equal(Path.GetFullPath(root), options.DataRoot);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "custom.db")), options.DatabasePath);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "custom-logs")), options.LogRoot);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "custom-artifacts")), options.ArtifactRoot);
    }

    [Fact]
    public void ArtifactHelpers_GroupByWorkItemThenExecutionOrTask()
    {
        var options = new StorageOptions
        {
            ArtifactRoot = Path.Combine(Path.GetTempPath(), "ado-worker-artifacts"),
        };

        var workItemRoot = options.GetWorkItemArtifactRoot(28639);
        var executionRoot = options.GetExecutionArtifactRoot(28639, "de2f6ffd2c034cef9a4abfe1a4bcd84d");
        var taskRoot = options.GetTaskArtifactRoot(28639, 28883);

        Assert.Equal(Path.Combine(options.ArtifactRoot, "28639"), workItemRoot);
        Assert.Equal(Path.Combine(options.ArtifactRoot, "28639", "executions", "de2f6ffd2c034cef9a4abfe1a4bcd84d"), executionRoot);
        Assert.Equal(Path.Combine(options.ArtifactRoot, "28639", "tasks", "28883"), taskRoot);
    }
}
