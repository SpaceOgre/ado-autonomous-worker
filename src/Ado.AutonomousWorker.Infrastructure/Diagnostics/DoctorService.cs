using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Services;
using Ado.AutonomousWorker.Infrastructure.Execution;
using Ado.AutonomousWorker.Infrastructure.Workspaces;
using Ado.AutonomousWorker.Infrastructure.State;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Diagnostics;

public sealed class DoctorService(
    IAzureDevOpsClient azureDevOpsClient,
    IStateStore stateStore,
    IWorkspaceManager workspaceManager,
    ProcessCommandRunner commandRunner,
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
        checks.Add(await CheckRepoBranchAsync("Backend Branch", repositoryOptions.Value.Backend, cancellationToken));

        checks.Add(new DoctorCheckResult(
            "Frontend Repo",
            Directory.Exists(repositoryOptions.Value.Frontend.BaseClonePath),
            repositoryOptions.Value.Frontend.BaseClonePath));
        checks.Add(await CheckRepoBranchAsync("Frontend Branch", repositoryOptions.Value.Frontend, cancellationToken));

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

    private async Task<DoctorCheckResult> CheckRepoBranchAsync(string name, RepositoryProfile profile, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(profile.BaseClonePath))
        {
            return new DoctorCheckResult(name, false, $"Base clone path not found: {profile.BaseClonePath}");
        }

        var fetchResult = await commandRunner.RunAsync(
            "git",
            ["-C", profile.BaseClonePath, "fetch", "origin", profile.MainBranch],
            profile.BaseClonePath,
            Path.Combine(storageOptions.Value.ArtifactRoot, $"doctor-{profile.Kind.ToString().ToLowerInvariant()}-fetch.log"),
            environmentVariables: null,
            activityName: null,
            outputCallback: null,
            cancellationToken);

        if (!fetchResult.Success)
        {
            return new DoctorCheckResult(name, false, $"Fetch failed for origin/{profile.MainBranch}: {fetchResult.StandardError.Trim()}");
        }

        var verifyResult = await commandRunner.RunAsync(
            "git",
            ["-C", profile.BaseClonePath, "show-ref", "--verify", "--quiet", $"refs/remotes/origin/{profile.MainBranch}"],
            profile.BaseClonePath,
            Path.Combine(storageOptions.Value.ArtifactRoot, $"doctor-{profile.Kind.ToString().ToLowerInvariant()}-branch.log"),
            environmentVariables: null,
            activityName: null,
            outputCallback: null,
            cancellationToken);

        return verifyResult.Success
            ? new DoctorCheckResult(name, true, $"origin/{profile.MainBranch}")
            : new DoctorCheckResult(name, false, $"Missing remote branch origin/{profile.MainBranch}");
    }

    private static DoctorCheckResult CheckCommand(string name, string commandPath)
    {
        if (string.IsNullOrWhiteSpace(commandPath))
        {
            return new DoctorCheckResult(name, false, "No command path configured");
        }

        var resolvedPath = CommandPathResolver.ResolveOrOriginal(commandPath);
        var exists = CommandPathResolver.Exists(commandPath);
        return new DoctorCheckResult(name, exists, resolvedPath);
    }
}
