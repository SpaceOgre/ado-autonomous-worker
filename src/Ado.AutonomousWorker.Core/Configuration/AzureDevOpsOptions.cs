namespace Ado.AutonomousWorker.Core.Configuration;

public sealed class AzureDevOpsOptions
{
    public string OrganizationUrl { get; set; } = string.Empty;

    public string Project { get; set; } = "PRIIS";

    public string Team { get; set; } = "PRIIS Team";

    public string PersonalAccessToken { get; set; } = string.Empty;

    public string DefaultAssignee { get; set; } = string.Empty;
}
