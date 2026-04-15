using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Infrastructure.Execution;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Diagnostics;

public sealed class DoctorService(
    IAzureDevOpsClient azureDevOpsClient,
    IStateStore stateStore,
    IWorkspaceManager workspaceManager,
    ProcessCommandRunner commandRunner,
    IOptions<AgentOptions> agentOptions,
    IOptions<AzureDevOpsOptions> azureDevOpsOptions,
    IOptions<RepositoryOptions> repositoryOptions,
    IOptions<StorageOptions> storageOptions,
    IOptions<DeployOptions> deployOptions)
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
        checks.Add(CheckAgentCommand("Plan Agent", agentOptions.Value.Plan));
        checks.Add(CheckAgentCommand("Code Agent", agentOptions.Value.Code));
        checks.Add(CheckAgentCommand("Review Agent", agentOptions.Value.Review));

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

        if (string.IsNullOrWhiteSpace(azureDevOpsOptions.Value.PersonalAccessToken))
        {
            var configPath = Path.Combine(StorageOptions.DefaultAppFolder, "config.jsonc");
            checks.Add(new DoctorCheckResult(
                "Azure DevOps",
                false,
                $"PersonalAccessToken is not set. Edit {configPath} to configure it."));
        }
        else
        {
            try
            {
                var sprint = await azureDevOpsClient.GetCurrentSprintPathAsync(cancellationToken);
                checks.Add(new DoctorCheckResult("Azure DevOps", !string.IsNullOrWhiteSpace(sprint), sprint ?? "No sprint returned"));
            }
            catch (Exception ex)
            {
                checks.Add(new DoctorCheckResult("Azure DevOps", false, ex.Message));
            }
        }

        // Pipeline checks (only when pipeline IDs are configured)
        var deploy = deployOptions.Value;
        if (deploy.BackendPipelineId > 0)
        {
            checks.Add(await CheckPipelineAsync("Backend Pipeline", deploy.BackendPipelineId, cancellationToken));
        }
        else
        {
            checks.Add(new DoctorCheckResult("Backend Pipeline", false, "Deploy:BackendPipelineId is not configured."));
        }

        if (deploy.FrontendPipelineId > 0)
        {
            checks.Add(await CheckPipelineAsync("Frontend Pipeline", deploy.FrontendPipelineId, cancellationToken));
        }
        else
        {
            checks.Add(new DoctorCheckResult("Frontend Pipeline", false, "Deploy:FrontendPipelineId is not configured."));
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

    private static DoctorCheckResult CheckAgentCommand(string name, AgentRoleConfig config)
    {
        var defaultCommand = config.Provider == AgentProvider.Claude ? "claude" : "codex";
        var commandPath = string.IsNullOrWhiteSpace(config.CommandPath) ? defaultCommand : config.CommandPath;
        var resolvedPath = CommandPathResolver.ResolveOrOriginal(commandPath);
        var exists = CommandPathResolver.Exists(commandPath);
        return new DoctorCheckResult(name, exists, resolvedPath);
    }

    private async Task<DoctorCheckResult> CheckPipelineAsync(string name, int pipelineId, CancellationToken cancellationToken)
    {
        try
        {
            var pipelineName = await azureDevOpsClient.TryGetPipelineNameAsync(pipelineId, cancellationToken);
            return pipelineName is not null
                ? new DoctorCheckResult(name, true, $"id={pipelineId}  name={pipelineName}")
                : new DoctorCheckResult(name, false, $"Pipeline id={pipelineId} not found in Azure DevOps.");
        }
        catch (Exception ex)
        {
            return new DoctorCheckResult(name, false, ex.Message);
        }
    }
}
