using Ado.AutonomousWorker.Core.Configuration;

namespace Ado.AutonomousWorker.Tests;

public sealed class StorageOptionsTests
{
    [Fact]
    public void NormalizePaths_MapsLegacyRelativeDefaultsIntoLocalAppData()
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
}
