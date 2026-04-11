using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Services;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Services;

public sealed class DoctorService(
    IAzureDevOpsClient azureDevOpsClient,
    IStateStore stateStore,
    IWorkspaceManager workspaceManager,
    IOptions<AgentOptions> agentOptions,
    IOptions<RepositoryOptions> repositoryOptions,
    IOptions<StorageOptions> storageOptions)
{
    public async Task<IReadOnlyList<DoctorCheckResult>> RunAsync(CancellationToken cancellationToken)
    {
        var checks = new List<DoctorCheckResult>();
        var storage = storageOptions.Value;

        Directory.CreateDirectory(storage.DataRoot);
        Directory.CreateDirectory(storage.LogRoot);
        Directory.CreateDirectory(storage.ArtifactRoot);

        await stateStore.InitializeAsync(cancellationToken);
        await workspaceManager.PrepareAsync(cancellationToken);

        checks.Add(new DoctorCheckResult("Storage", true, storage.DataRoot));
        checks.Add(CheckCommand("Plan Agent", agentOptions.Value.Plan.CommandPath));
        checks.Add(CheckCommand("Code Agent", agentOptions.Value.Code.CommandPath));
        checks.Add(CheckCommand("Review Agent", agentOptions.Value.Review.CommandPath));

        checks.Add(new DoctorCheckResult(
            "Backend Repo",
            Directory.Exists(repositoryOptions.Value.Backend.BaseClonePath),
            repositoryOptions.Value.Backend.BaseClonePath));

        checks.Add(new DoctorCheckResult(
            "Frontend Repo",
            Directory.Exists(repositoryOptions.Value.Frontend.BaseClonePath),
            repositoryOptions.Value.Frontend.BaseClonePath));

        try
        {
            var sprint = await azureDevOpsClient.GetCurrentSprintPathAsync(cancellationToken);
            checks.Add(new DoctorCheckResult("Azure DevOps", !string.IsNullOrWhiteSpace(sprint), sprint ?? "No sprint returned"));
        }
        catch (Exception ex)
        {
            checks.Add(new DoctorCheckResult("Azure DevOps", false, ex.Message));
        }

        return checks;
    }

    private static DoctorCheckResult CheckCommand(string name, string commandPath)
    {
        if (string.IsNullOrWhiteSpace(commandPath))
        {
            return new DoctorCheckResult(name, false, "No command path configured");
        }

        var exists = File.Exists(commandPath) || CommandExistsOnPath(commandPath);
        return new DoctorCheckResult(name, exists, commandPath);
    }

    private static bool CommandExistsOnPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder, command);
            if (File.Exists(candidate) || File.Exists(candidate + ".exe") || File.Exists(candidate + ".cmd"))
            {
                return true;
            }
        }

        return false;
    }
}
