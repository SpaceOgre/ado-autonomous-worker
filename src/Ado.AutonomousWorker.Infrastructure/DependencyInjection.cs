using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Rules;
using Ado.AutonomousWorker.Infrastructure.Agents;
using Ado.AutonomousWorker.Infrastructure.AzureDevOps;
using Ado.AutonomousWorker.Infrastructure.Deploy;
using Ado.AutonomousWorker.Infrastructure.Diagnostics;
using Ado.AutonomousWorker.Infrastructure.Execution;
using Ado.AutonomousWorker.Infrastructure.Orchestration;
using Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;
using Ado.AutonomousWorker.Infrastructure.Queue;
using Ado.AutonomousWorker.Infrastructure.State;
using Ado.AutonomousWorker.Infrastructure.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAutonomousWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentOptions>(configuration.GetSection("Agents"));
        services.Configure<AzureDevOpsOptions>(configuration.GetSection("AzureDevOps"));
        services.Configure<RepositoryOptions>(configuration.GetSection("Repositories"));
        services.Configure<StorageOptions>(configuration.GetSection("Storage"));
        services.PostConfigure<StorageOptions>(options => options.NormalizePaths());
        services.Configure<WorkerOptions>(configuration.GetSection("Worker"));
        services.Configure<DeployOptions>(configuration.GetSection("Deploy"));

        services.AddDbContextFactory<WorkerDbContext>((provider, options) =>
        {
            var storage = provider.GetRequiredService<IOptions<StorageOptions>>().Value;
            Directory.CreateDirectory(Path.GetDirectoryName(storage.DatabasePath)!);
            options.UseSqlite($"Data Source={storage.DatabasePath}");
        }, ServiceLifetime.Singleton);

        services.AddSingleton<IRulesEngine, RulesEngine>();
        services.AddSingleton<IStateStore, SqliteStateStore>();
        services.AddSingleton<IWorkspaceManager, WorkspaceManager>();
        services.AddSingleton<IAgentRunnerFactory, AgentRunnerFactory>();
        services.AddSingleton<ExecutionJournal>();
        services.AddSingleton<TaskStateMachine>();
        services.AddSingleton<IPhaseHandler, WorkItemEligibilityHandler>();
        services.AddSingleton<IPhaseHandler, PrFixEligibilityHandler>();
        services.AddSingleton<IPhaseHandler, PlanningHandler>();
        services.AddSingleton<IPhaseHandler, WorkItemCodingHandler>();
        services.AddSingleton<IPhaseHandler, PrFixCodingHandler>();
        services.AddSingleton<IPhaseHandler, ReviewingHandler>();
        services.AddSingleton<IPhaseHandler, ApplyingReviewHandler>();
        services.AddSingleton<IPhaseHandler, ValidatingHandler>();
        services.AddSingleton<IPhaseHandler, WorkItemPublishingHandler>();
        services.AddSingleton<IPhaseHandler, PrFixPublishingHandler>();
        services.AddSingleton<ITaskOrchestrator, TaskOrchestrator>();
        services.AddSingleton<QueueInspector>();
        services.AddSingleton<DoctorService>();
        services.AddSingleton<DeployService>();
        services.AddSingleton<ProcessCommandRunner>();

        services.AddTransient<CodexRunner>();
        services.AddTransient<ClaudeRunner>();

        services.AddHttpClient<IAzureDevOpsClient, AzureDevOpsClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<AzureDevOpsOptions>>().Value;
            client.BaseAddress = new Uri(options.OrganizationUrl.TrimEnd('/') + "/");
            if (!string.IsNullOrWhiteSpace(options.PersonalAccessToken))
            {
                var token = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{options.PersonalAccessToken}"));
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
            }
        });

        return services;
    }
}
