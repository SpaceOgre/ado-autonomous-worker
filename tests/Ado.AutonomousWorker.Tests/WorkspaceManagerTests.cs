using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Infrastructure.Execution;
using Ado.AutonomousWorker.Infrastructure.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Tests;

public sealed class WorkspaceManagerTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "ado-worker-tests",
        Guid.NewGuid().ToString("N")
    );

    private readonly ProcessCommandRunner _runner = new(NullLogger<ProcessCommandRunner>.Instance);

    private string OriginPath => Path.Combine(_rootPath, "origin");
    private string BaseClonePath => Path.Combine(_rootPath, "clone");
    private string WorktreeRoot => Path.Combine(_rootPath, "wt");

    public WorkspaceManagerTests()
    {
        Directory.CreateDirectory(_rootPath);
    }

    [Test]
    public async Task CreateWorkspaceAsync_CreatesBranchWorktreeWithoutTouchingBaseClone()
    {
        await CreateMonorepoAsync();
        var manager = CreateManager();

        var workspace = await manager.CreateWorkspaceAsync(
            100,
            123,
            "Add export",
            preserveExistingWorkspace: false,
            CancellationToken.None
        );

        await Assert.That(workspace.BranchName).IsEqualTo("feature/123_add-export");
        await Assert.That(workspace.WorkingDirectory).IsEqualTo(Path.Combine(WorktreeRoot, "123_add-export"));
        await Assert
            .That(await GitAsync(workspace.WorkingDirectory, "branch", "--show-current"))
            .IsEqualTo("feature/123_add-export");
        await Assert.That(await GitAsync(BaseClonePath, "branch", "--show-current")).IsEqualTo("developer-branch");
    }

    [Test]
    public async Task GetChangedRepoKindsAsync_ReturnsHalvesWithModifiedOrUntrackedFiles()
    {
        await CreateMonorepoAsync();
        var manager = CreateManager();
        var workspace = await manager.CreateWorkspaceAsync(
            100,
            123,
            "fix",
            preserveExistingWorkspace: false,
            CancellationToken.None
        );
        var artifactDirectory = Path.Combine(_rootPath, "artifacts", "changed");

        await File.WriteAllTextAsync(Path.Combine(workspace.WorkingDirectory, "frontend", "app.ts"), "changed");
        var frontendOnly = await manager.GetChangedRepoKindsAsync(workspace, artifactDirectory, CancellationToken.None);

        await File.WriteAllTextAsync(Path.Combine(workspace.WorkingDirectory, "backend", "New.cs"), "new");
        var both = await manager.GetChangedRepoKindsAsync(workspace, artifactDirectory, CancellationToken.None);

        await Assert.That(frontendOnly).IsEquivalentTo([RepoKind.Frontend]);
        await Assert.That(both).IsEquivalentTo([RepoKind.Backend, RepoKind.Frontend]);
    }

    [Test]
    public async Task CreateWorkspaceAsync_RefusesToReplaceWorktreeWithUncommittedChanges()
    {
        await CreateMonorepoAsync();
        var manager = CreateManager();
        var workspace = await manager.CreateWorkspaceAsync(
            100,
            123,
            "fix",
            preserveExistingWorkspace: false,
            CancellationToken.None
        );
        var changedFile = Path.Combine(workspace.WorkingDirectory, "backend", "Api.cs");
        await File.WriteAllTextAsync(changedFile, "work in progress");

        await Assert
            .That(async () =>
                await manager.CreateWorkspaceAsync(
                    100,
                    123,
                    "fix",
                    preserveExistingWorkspace: false,
                    CancellationToken.None
                )
            )
            .Throws<InvalidOperationException>();
        await Assert.That(await File.ReadAllTextAsync(changedFile)).IsEqualTo("work in progress");
    }

    private WorkspaceManager CreateManager()
    {
        var storage = new StorageOptions { DataRoot = Path.Combine(_rootPath, "data") };
        storage.NormalizePaths();

        var repository = new RepositoryOptions
        {
            BaseClonePath = BaseClonePath,
            MainBranch = "main",
            WorktreeRoot = WorktreeRoot,
            Backend = new RepositoryProfile { Kind = RepoKind.Backend, Folder = "backend" },
            Frontend = new RepositoryProfile { Kind = RepoKind.Frontend, Folder = "frontend" },
        };

        return new WorkspaceManager(
            Options.Create(repository),
            Options.Create(storage),
            _runner,
            NullLogger<WorkspaceManager>.Instance
        );
    }

    // An origin with backend/ and frontend/ halves, and a clone that is checked out on another branch,
    // like a developer's own checkout.
    private async Task CreateMonorepoAsync()
    {
        Directory.CreateDirectory(Path.Combine(OriginPath, "backend"));
        Directory.CreateDirectory(Path.Combine(OriginPath, "frontend"));
        await File.WriteAllTextAsync(Path.Combine(OriginPath, "backend", "Api.cs"), "api");
        await File.WriteAllTextAsync(Path.Combine(OriginPath, "frontend", "app.ts"), "app");

        await GitAsync(OriginPath, "init", "-b", "main");
        await GitAsync(OriginPath, "add", "-A");
        await GitAsync(OriginPath, "-c", "user.name=test", "-c", "user.email=test@example.com", "commit", "-m", "init");
        await GitAsync(_rootPath, "clone", OriginPath, BaseClonePath);
        await GitAsync(BaseClonePath, "checkout", "-b", "developer-branch");
    }

    private async Task<string> GitAsync(string workingDirectory, params string[] arguments)
    {
        var result = await _runner.RunAsync(
            "git",
            ["-C", workingDirectory, .. arguments],
            workingDirectory,
            Path.Combine(_rootPath, "logs", $"{Guid.NewGuid():N}.log"),
            environmentVariables: null,
            activityName: null,
            outputCallback: null,
            CancellationToken.None
        );

        if (!result.Success)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.StandardError}");

        return result.StandardOutput.Trim();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootPath))
            {
                foreach (var file in Directory.EnumerateFiles(_rootPath, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(_rootPath, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
