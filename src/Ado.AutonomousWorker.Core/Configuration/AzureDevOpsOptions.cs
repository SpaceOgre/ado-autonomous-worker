namespace Ado.AutonomousWorker.Core.Configuration;

public sealed class AzureDevOpsOptions
{
    public string OrganizationUrl { get; set; } = string.Empty;

    public string Project { get; set; } = "PRIIS";

    public string Team { get; set; } = "PRIIS Team";

    public string PersonalAccessToken { get; set; } = string.Empty;

    public string DefaultAssignee { get; set; } = string.Empty;

    public string? WorkItemUrl(int workItemId) =>
        string.IsNullOrWhiteSpace(OrganizationUrl)
            ? null
            : $"{OrganizationUrl.TrimEnd('/')}/{Project}/_workitems/edit/{workItemId}";

    public string? PullRequestUrl(string? repositoryName, int pullRequestId) =>
        string.IsNullOrWhiteSpace(repositoryName) || string.IsNullOrWhiteSpace(OrganizationUrl)
            ? null
            : $"{OrganizationUrl.TrimEnd('/')}/{Project}/_git/{repositoryName}/pullrequest/{pullRequestId}";
}
