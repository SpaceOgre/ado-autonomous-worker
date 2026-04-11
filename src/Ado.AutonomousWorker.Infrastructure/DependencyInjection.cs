using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Services;
using Ado.AutonomousWorker.Infrastructure.Execution;
using Ado.AutonomousWorker.Infrastructure.Services;
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
        services.Configure<WorkerOptions>(configuration.GetSection("Worker"));

        services.AddSingleton<IRulesEngine, RulesEngine>();
        services.AddSingleton<IStateStore, SqliteStateStore>();
        services.AddSingleton<IWorkspaceManager, WorkspaceManager>();
        services.AddSingleton<IAgentRunnerFactory, AgentRunnerFactory>();
        services.AddSingleton<ITaskOrchestrator, TaskOrchestrator>();
        services.AddSingleton<QueueInspector>();
        services.AddSingleton<DoctorService>();
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
