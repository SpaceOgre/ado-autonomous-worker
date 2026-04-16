namespace Ado.AutonomousWorker.Core.Configuration;

public sealed class DeployOptions
{
    /// <summary>Azure DevOps pipeline definition ID for the backend pipeline.</summary>
    public int BackendPipelineId { get; set; }

    /// <summary>Azure DevOps pipeline definition ID for the frontend pipeline.</summary>
    public int FrontendPipelineId { get; set; }

    /// <summary>Branch to trigger both pipelines from.</summary>
    public string Branch { get; set; } = "main";

    /// <summary>
    /// How often (in seconds) to poll pipeline run status while waiting for completion.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum total time (in minutes) to wait for the pipeline runs before timing out.
    /// </summary>
    public int TimeoutMinutes { get; set; } = 60;

    /// <summary>
    /// Email address of the user to assign when transitioning work items to "Ready for Test".
    /// </summary>
    public string ReadyForTestAssignee { get; set; } = string.Empty;
}
