using Ado.AutonomousWorker.Core.Configuration;

namespace Ado.AutonomousWorker.Tests;

public sealed class StorageOptionsTests
{
    [Test]
    public async Task NormalizePaths_DefaultsToLocalAppData()
    {
        var options = new StorageOptions();

        options.NormalizePaths();

        var expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ado.AutonomousWorker"
        );

        await Assert.That(options.DataRoot).IsEqualTo(Path.GetFullPath(expectedRoot));
        await Assert.That(options.DatabasePath).IsEqualTo(Path.Combine(Path.GetFullPath(expectedRoot), "worker.db"));
        await Assert.That(options.LogRoot).IsEqualTo(Path.Combine(Path.GetFullPath(expectedRoot), "logs"));
        await Assert.That(options.ArtifactRoot).IsEqualTo(Path.Combine(Path.GetFullPath(expectedRoot), "artifacts"));
    }

    [Test]
    public async Task NormalizePaths_WithBaseOverride_ResolvesRelativeDefaultsUnderOverride()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "ado-worker-dev-base");
        var options = new StorageOptions();

        options.NormalizePaths(baseOverride: baseDir);

        await Assert.That(options.DataRoot).IsEqualTo(Path.GetFullPath(baseDir));
        await Assert.That(options.DatabasePath).IsEqualTo(Path.Combine(Path.GetFullPath(baseDir), "worker.db"));
        await Assert.That(options.LogRoot).IsEqualTo(Path.Combine(Path.GetFullPath(baseDir), "logs"));
        await Assert.That(options.ArtifactRoot).IsEqualTo(Path.Combine(Path.GetFullPath(baseDir), "artifacts"));
    }

    [Test]
    public async Task NormalizePaths_KeepsAbsoluteOverrides()
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

        await Assert.That(options.DataRoot).IsEqualTo(Path.GetFullPath(root));
        await Assert.That(options.DatabasePath).IsEqualTo(Path.GetFullPath(Path.Combine(root, "custom.db")));
        await Assert.That(options.LogRoot).IsEqualTo(Path.GetFullPath(Path.Combine(root, "custom-logs")));
        await Assert.That(options.ArtifactRoot).IsEqualTo(Path.GetFullPath(Path.Combine(root, "custom-artifacts")));
    }

    [Test]
    public async Task ArtifactHelpers_GroupByWorkItemThenExecutionOrTask()
    {
        var options = new StorageOptions { ArtifactRoot = Path.Combine(Path.GetTempPath(), "ado-worker-artifacts") };

        var workItemRoot = options.GetWorkItemArtifactRoot(28639);
        var executionRoot = options.GetExecutionArtifactRoot(28639, "de2f6ffd2c034cef9a4abfe1a4bcd84d");
        var taskRoot = options.GetTaskArtifactRoot(28639, 28883);

        await Assert.That(workItemRoot).IsEqualTo(Path.Combine(options.ArtifactRoot, "28639"));
        await Assert
            .That(executionRoot)
            .IsEqualTo(Path.Combine(options.ArtifactRoot, "28639", "executions", "de2f6ffd2c034cef9a4abfe1a4bcd84d"));
        await Assert.That(taskRoot).IsEqualTo(Path.Combine(options.ArtifactRoot, "28639", "tasks", "28883"));
    }
}
