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
    IOptions<DeployOptions> deployOptions
)
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

        var repository = repositoryOptions.Value;
        checks.Add(
            new DoctorCheckResult("Repository", Directory.Exists(repository.BaseClonePath), repository.BaseClonePath)
        );
        checks.Add(CheckRepoFolder("Backend Folder", repository, repository.Backend));
        checks.Add(CheckRepoFolder("Frontend Folder", repository, repository.Frontend));
        checks.Add(await CheckRepoBranchAsync("Main Branch", repository, cancellationToken));
        checks.Add(
            new DoctorCheckResult("Worktree Root", Directory.Exists(repository.WorktreeRoot), repository.WorktreeRoot)
        );

        var adoConnected = false;
        if (string.IsNullOrWhiteSpace(azureDevOpsOptions.Value.PersonalAccessToken))
        {
            var configPath = Path.Combine(StorageOptions.DefaultAppFolder, "config.jsonc");
            checks.Add(
                new DoctorCheckResult(
                    "Azure DevOps",
                    false,
                    $"PersonalAccessToken is not set. Edit {configPath} to configure it."
                )
            );
        }
        else
        {
            try
            {
                var sprint = await azureDevOpsClient.GetCurrentSprintPathAsync(cancellationToken);
                adoConnected = !string.IsNullOrWhiteSpace(sprint);
                checks.Add(new DoctorCheckResult("Azure DevOps", adoConnected, sprint ?? "No sprint returned"));
            }
            catch (Exception ex)
            {
                checks.Add(new DoctorCheckResult("Azure DevOps", false, ex.Message));
            }
        }

        // Pipeline check: only run when the ID is configured and ADO connectivity has been confirmed.
        // If ADO auth failed, pipeline API calls would also fail — skip it to avoid misleading errors.
        var deploy = deployOptions.Value;
        if (deploy.PipelineId > 0)
        {
            checks.Add(
                adoConnected
                    ? await CheckPipelineAsync("Deploy Pipeline", deploy.PipelineId, cancellationToken)
                    : new DoctorCheckResult(
                        "Deploy Pipeline",
                        false,
                        "Skipped — Azure DevOps connectivity check did not pass."
                    )
            );
        }
        else
        {
            checks.Add(new DoctorCheckResult("Deploy Pipeline", true, "Not configured — skipped."));
        }

        return checks;
    }

    private static DoctorCheckResult CheckRepoFolder(
        string name,
        RepositoryOptions repository,
        RepositoryProfile profile
    )
    {
        var folderPath = Path.Combine(repository.BaseClonePath, profile.Folder);
        return new DoctorCheckResult(name, Directory.Exists(folderPath), folderPath);
    }

    private async Task<DoctorCheckResult> CheckRepoBranchAsync(
        string name,
        RepositoryOptions repository,
        CancellationToken cancellationToken
    )
    {
        if (!Directory.Exists(repository.BaseClonePath))
        {
            return new DoctorCheckResult(name, false, $"Base clone path not found: {repository.BaseClonePath}");
        }

        var fetchResult = await commandRunner.RunAsync(
            "git",
            ["-C", repository.BaseClonePath, "fetch", "origin", repository.MainBranch],
            repository.BaseClonePath,
            Path.Combine(storageOptions.Value.ArtifactRoot, "doctor-fetch.log"),
            environmentVariables: null,
            activityName: null,
            outputCallback: null,
            cancellationToken
        );

        if (!fetchResult.Success)
        {
            return new DoctorCheckResult(
                name,
                false,
                $"Fetch failed for origin/{repository.MainBranch}: {fetchResult.StandardError.Trim()}"
            );
        }

        var verifyResult = await commandRunner.RunAsync(
            "git",
            [
                "-C",
                repository.BaseClonePath,
                "show-ref",
                "--verify",
                "--quiet",
                $"refs/remotes/origin/{repository.MainBranch}",
            ],
            repository.BaseClonePath,
            Path.Combine(storageOptions.Value.ArtifactRoot, "doctor-branch.log"),
            environmentVariables: null,
            activityName: null,
            outputCallback: null,
            cancellationToken
        );

        return verifyResult.Success
            ? new DoctorCheckResult(name, true, $"origin/{repository.MainBranch}")
            : new DoctorCheckResult(name, false, $"Missing remote branch origin/{repository.MainBranch}");
    }

    private static DoctorCheckResult CheckAgentCommand(string name, AgentRoleConfig config)
    {
        var defaultCommand = config.Provider == AgentProvider.Claude ? "claude" : "codex";
        var commandPath = string.IsNullOrWhiteSpace(config.CommandPath) ? defaultCommand : config.CommandPath;
        var resolvedPath = CommandPathResolver.ResolveOrOriginal(commandPath);
        var exists = CommandPathResolver.Exists(commandPath);
        return new DoctorCheckResult(name, exists, resolvedPath);
    }

    private async Task<DoctorCheckResult> CheckPipelineAsync(
        string name,
        int pipelineId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var pipelineName = await azureDevOpsClient.TryGetPipelineNameAsync(pipelineId, cancellationToken);
            return pipelineName is not null
                ? new DoctorCheckResult(name, true, $"id={pipelineId}  name={pipelineName}")
                : new DoctorCheckResult(name, false, $"Pipeline id={pipelineId} not found in Azure DevOps.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DoctorCheckResult(name, false, ex.Message);
        }
    }
}
