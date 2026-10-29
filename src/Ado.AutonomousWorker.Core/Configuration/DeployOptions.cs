namespace Ado.AutonomousWorker.Core.Configuration;

public sealed class DeployOptions
{
    /// <summary>
    /// Azure DevOps pipeline definition ID of the monorepo deploy pipeline. One run deploys the backend
    /// and/or frontend, selected through its <c>deployBackend</c> and <c>deployFrontend</c> parameters.
    /// </summary>
    public int PipelineId { get; set; }

    /// <summary>Value passed to the deploy pipeline's <c>environment</c> parameter.</summary>
    public string Environment { get; set; } = "Test";

    /// <summary>Branch to trigger the pipeline from.</summary>
    public string Branch { get; set; } = "main";

    /// <summary>
    /// How often (in seconds) to poll pipeline run status while waiting for completion.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum total time (in minutes) to wait for the pipeline run before timing out.
    /// </summary>
    public int TimeoutMinutes { get; set; } = 60;

    /// <summary>
    /// Email address of the user to assign when transitioning work items to "Ready for test".
    /// </summary>
    public string ReadyForTestAssignee { get; set; } = string.Empty;
}
