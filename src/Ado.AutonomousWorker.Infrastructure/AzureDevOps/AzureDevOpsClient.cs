using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.AzureDevOps;

public sealed class AzureDevOpsClient(
    HttpClient httpClient,
    IOptions<AzureDevOpsOptions> options,
    ILogger<AzureDevOpsClient> logger) : IAzureDevOpsClient
{
    private const string ApiVersion = "7.1";

    private readonly HttpClient _httpClient = httpClient;
    private readonly AzureDevOpsOptions _options = options.Value;
    private readonly ILogger<AzureDevOpsClient> _logger = logger;

    public async Task<string?> GetCurrentSprintPathAsync(CancellationToken cancellationToken)
    {
        var team = Uri.EscapeDataString(_options.Team);
        var response = await _httpClient.GetAsync(
            $"{_options.Project}/{team}/_apis/work/teamsettings/iterations?$timeframe=current&api-version={ApiVersion}",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        foreach (var item in document.RootElement.GetProperty("value").EnumerateArray())
        {
            if (item.TryGetProperty("attributes", out var attributes) &&
                attributes.TryGetProperty("timeFrame", out var timeFrame) &&
                string.Equals(timeFrame.GetString(), "current", StringComparison.OrdinalIgnoreCase) &&
                item.TryGetProperty("path", out var path))
            {
                return path.GetString();
            }
        }

        return null;
    }

    public Task<IReadOnlyList<WorkItemSummary>> QueryAiWorkAsync(CancellationToken cancellationToken)
    {
        var query = $"""
        SELECT [System.Id], [System.Title], [System.State], [System.AssignedTo], [Microsoft.VSTS.Common.StackRank], [Microsoft.VSTS.Common.Priority], [System.WorkItemType]
        FROM WorkItems
        WHERE ([System.WorkItemType] = 'User Story' OR [System.WorkItemType] = 'Bug')
          AND [System.Tags] CONTAINS 'AI-Work'
          AND NOT [System.Tags] CONTAINS 'AI-Failed'
          AND [System.State] = 'New'
          AND ([System.AssignedTo] = '' OR [System.AssignedTo] = '{_options.DefaultAssignee}')
          AND [System.TeamProject] = '{_options.Project}'
        ORDER BY [Microsoft.VSTS.Common.StackRank] ASC
        """;

        return QueryByWiqlAsync(query, cancellationToken);
    }

    public Task<IReadOnlyList<WorkItemSummary>> QueryAiReworkAsync(CancellationToken cancellationToken)
    {
        var query = $"""
        SELECT [System.Id], [System.Title], [System.State], [System.AssignedTo], [Microsoft.VSTS.Common.StackRank], [Microsoft.VSTS.Common.Priority], [System.WorkItemType]
        FROM WorkItems
        WHERE ([System.WorkItemType] = 'User Story' OR [System.WorkItemType] = 'Bug')
          AND [System.Tags] CONTAINS 'AI-Rework'
          AND NOT [System.Tags] CONTAINS 'AI-Failed'
          AND [System.State] <> 'Closed'
          AND [System.State] <> 'Removed'
          AND [System.AssignedTo] = '{_options.DefaultAssignee}'
          AND [System.TeamProject] = '{_options.Project}'
        ORDER BY [Microsoft.VSTS.Common.StackRank] ASC
        """;

        return QueryByWiqlAsync(query, cancellationToken);
    }

    public async Task<WorkItemDetails> GetWorkItemAsync(int workItemId, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"{_options.Project}/_apis/wit/workitems/{workItemId}?$expand=Relations&api-version={ApiVersion}",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var fields = root.GetProperty("fields");
        var tags = ParseTags(fields.TryGetProperty("System.Tags", out var tagElement) ? tagElement.GetString() : null);
        var childIds = new List<int>();

        if (root.TryGetProperty("relations", out var relationsElement))
        {
            foreach (var relation in relationsElement.EnumerateArray())
            {
                if (!relation.TryGetProperty("rel", out var relElement) ||
                    !string.Equals(relElement.GetString(), "System.LinkTypes.Hierarchy-Forward", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (relation.TryGetProperty("url", out var urlElement) &&
                    int.TryParse(urlElement.GetString()?.Split('/').Last(), out var childId))
                {
                    childIds.Add(childId);
                }
            }
        }

        var children = childIds.Count == 0
            ? []
            : await GetChildTasksAsync(childIds, cancellationToken);

        return new WorkItemDetails
        {
            WorkItemId = root.TryGetProperty("id", out var idElement) ? idElement.GetInt32() : workItemId,
            WorkItemType = ParseWorkItemType(fields.TryGetProperty("System.WorkItemType", out var typeElement) ? typeElement.GetString() : null),
            Title = fields.TryGetProperty("System.Title", out var titleElement) ? titleElement.GetString() ?? string.Empty : string.Empty,
            State = fields.TryGetProperty("System.State", out var stateElement) ? stateElement.GetString() ?? string.Empty : string.Empty,
            AssignedTo = TryReadIdentityString(fields, "System.AssignedTo"),
            IterationPath = fields.TryGetProperty("System.IterationPath", out var iterationElement) ? iterationElement.GetString() ?? string.Empty : string.Empty,
            Description = fields.TryGetProperty("System.Description", out var descriptionElement) ? descriptionElement.GetString() ?? string.Empty : string.Empty,
            AcceptanceCriteria = fields.TryGetProperty("Microsoft.VSTS.Common.AcceptanceCriteria", out var acceptanceElement) ? acceptanceElement.GetString() ?? string.Empty : string.Empty,
            Tags = tags,
            ChildTasks = children,
            Kind = tags.Contains("AI-Rework", StringComparer.OrdinalIgnoreCase) ? QueueItemKind.AiRework : QueueItemKind.AiWork,
        };
    }

    public Task UpdateWorkItemStateAsync(int workItemId, string state, CancellationToken cancellationToken)
        => PatchFieldsAsync(workItemId, [PatchField("/fields/System.State", state)], cancellationToken);

    public Task AssignWorkItemAsync(int workItemId, string assignee, CancellationToken cancellationToken)
        => PatchFieldsAsync(workItemId, [PatchField("/fields/System.AssignedTo", assignee)], cancellationToken);

    public Task UpdateIterationPathAsync(int workItemId, string iterationPath, CancellationToken cancellationToken)
        => PatchFieldsAsync(workItemId, [PatchField("/fields/System.IterationPath", iterationPath)], cancellationToken);

    public Task UpdateTagsAsync(int workItemId, IReadOnlyCollection<string> tags, CancellationToken cancellationToken)
        => PatchFieldsAsync(workItemId, [PatchField("/fields/System.Tags", string.Join("; ", tags))], cancellationToken);

    public async Task<int> CreateChildTaskAsync(int parentWorkItemId, string title, IReadOnlyCollection<string> tags, string iterationPath, string assignee, CancellationToken cancellationToken)
    {
        var createDocument = new object[]
        {
            PatchField("/fields/System.Title", title),
            PatchField("/fields/System.Tags", string.Join("; ", tags)),
            PatchField("/fields/System.IterationPath", iterationPath),
            PatchField("/fields/System.AssignedTo", assignee),
        };

        using var request = BuildPatchRequest(
            $"{_options.Project}/_apis/wit/workitems/$Task?api-version={ApiVersion}",
            createDocument);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var childTaskId = document.RootElement.GetProperty("id").GetInt32();

        await UpdateWorkItemStateAsync(childTaskId, "Active", cancellationToken);
        await PatchFieldsAsync(childTaskId, [PatchRelation(parentWorkItemId)], cancellationToken);
        return childTaskId;
    }

    public async Task<PullRequestInfo> CreateDraftPullRequestAsync(string repositoryName, string sourceBranch, string targetBranch, string title, string description, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.Project}/_apis/git/repositories/{repositoryName}/pullrequests?api-version={ApiVersion}",
            new
            {
                sourceRefName = $"refs/heads/{sourceBranch}",
                targetRefName = $"refs/heads/{targetBranch}",
                title,
                description,
                isDraft = true,
            },
            cancellationToken);

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var prId = root.GetProperty("pullRequestId").GetInt32();
        var url = new Uri(root.GetProperty("url").GetString() ?? throw new InvalidOperationException("Azure DevOps did not return a pull request URL."));
        var projectId = root.GetProperty("repository").GetProperty("project").GetProperty("id").GetString();
        var repoId = root.GetProperty("repository").GetProperty("id").GetString();
        var artifactUri = projectId is null || repoId is null
            ? null
            : $"vstfs:///Git/PullRequestId/{projectId}%2f{repoId}%2f{prId}";

        return new PullRequestInfo(prId, title, url, artifactUri);
    }

    public async Task LinkPullRequestAsync(int workItemId, PullRequestInfo pullRequest, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pullRequest.ArtifactUri))
        {
            await AddCommentAsync(workItemId, $"Draft PR: {pullRequest.Url}", cancellationToken);
            return;
        }

        await PatchFieldsAsync(workItemId,
        [
            new
            {
                op = "add",
                path = "/relations/-",
                value = new
                {
                    rel = "ArtifactLink",
                    url = pullRequest.ArtifactUri,
                    attributes = new { name = "Pull Request" },
                },
            },
        ], cancellationToken);
    }

    public async Task AddCommentAsync(int workItemId, string comment, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.Project}/_apis/wit/workItems/{workItemId}/comments?api-version=7.1-preview.3",
            new { text = comment },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<IReadOnlyList<WorkItemSummary>> QueryByWiqlAsync(string query, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.Project}/_apis/wit/wiql?api-version={ApiVersion}",
            new { query },
            cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var ids = document.RootElement.GetProperty("workItems")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetInt32())
            .ToArray();

        return ids.Length == 0
            ? []
            : await GetWorkItemSummariesAsync(ids, cancellationToken);
    }

    private async Task<IReadOnlyList<WorkItemSummary>> GetWorkItemSummariesAsync(IReadOnlyList<int> ids, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.Project}/_apis/wit/workitemsbatch?api-version={ApiVersion}",
            new
            {
                ids,
                fields = new[]
                {
                    "System.Id",
                    "System.WorkItemType",
                    "System.Title",
                    "System.State",
                    "System.AssignedTo",
                    "Microsoft.VSTS.Common.StackRank",
                    "Microsoft.VSTS.Common.Priority",
                },
            },
            cancellationToken);

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var items = new List<WorkItemSummary>();
        foreach (var item in document.RootElement.GetProperty("value").EnumerateArray())
        {
            var fields = item.GetProperty("fields");
            items.Add(new WorkItemSummary(
                fields.GetProperty("System.Id").GetInt32(),
                ParseWorkItemType(fields.TryGetProperty("System.WorkItemType", out var typeElement) ? typeElement.GetString() : null),
                fields.GetProperty("System.Title").GetString() ?? string.Empty,
                fields.GetProperty("System.State").GetString() ?? string.Empty,
                TryReadIdentityString(fields, "System.AssignedTo"),
                fields.TryGetProperty("Microsoft.VSTS.Common.StackRank", out var rankElement) ? ParseStackRank(rankElement) : double.MaxValue,
                fields.TryGetProperty("Microsoft.VSTS.Common.Priority", out var priorityElement) ? ParsePriority(priorityElement) : null));
        }

        return items;
    }

    private async Task<IReadOnlyList<ChildTaskRecord>> GetChildTasksAsync(IReadOnlyList<int> ids, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.Project}/_apis/wit/workitemsbatch?api-version={ApiVersion}",
            new
            {
                ids,
                fields = new[] { "System.Id", "System.Title", "System.State", "System.Tags" },
            },
            cancellationToken);

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var children = new List<ChildTaskRecord>();
        foreach (var item in document.RootElement.GetProperty("value").EnumerateArray())
        {
            var fields = item.GetProperty("fields");
            var title = fields.GetProperty("System.Title").GetString() ?? string.Empty;
            var tags = ParseTags(fields.TryGetProperty("System.Tags", out var tagsElement) ? tagsElement.GetString() : null);
            var repoKind = tags.Contains("Frontend", StringComparer.OrdinalIgnoreCase) || title.Contains("frontend", StringComparison.OrdinalIgnoreCase)
                ? RepoKind.Frontend
                : RepoKind.Backend;

            children.Add(new ChildTaskRecord(
                fields.GetProperty("System.Id").GetInt32(),
                repoKind,
                title,
                fields.GetProperty("System.State").GetString() ?? string.Empty));
        }

        return children;
    }

    private async Task PatchFieldsAsync(int workItemId, object[] document, CancellationToken cancellationToken)
    {
        using var request = BuildPatchRequest(
            $"{_options.Project}/_apis/wit/workitems/{workItemId}?api-version={ApiVersion}",
            document);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        _logger.LogDebug("Patched work item {WorkItemId}", workItemId);
    }

    private static HttpRequestMessage BuildPatchRequest(string route, object[] document)
    {
        return new HttpRequestMessage(new HttpMethod("PATCH"), route)
        {
            Content = new StringContent(JsonSerializer.Serialize(document), Encoding.UTF8, "application/json-patch+json"),
        };
    }

    private static object PatchField(string path, object value) => new { op = "add", path, value };

    private object PatchRelation(int parentWorkItemId) => new
    {
        op = "add",
        path = "/relations/-",
        value = new
        {
            rel = "System.LinkTypes.Hierarchy-Reverse",
            url = $"{_options.OrganizationUrl.TrimEnd('/')}/{_options.Project}/_apis/wit/workItems/{parentWorkItemId}",
        },
    };

    private static string? TryReadIdentityString(JsonElement fields, string propertyName)
    {
        if (!fields.TryGetProperty(propertyName, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Object when element.TryGetProperty("uniqueName", out var uniqueName) => uniqueName.GetString(),
            JsonValueKind.Object when element.TryGetProperty("displayName", out var displayName) => displayName.GetString(),
            _ => null,
        };
    }

    private static IReadOnlyList<string> ParseTags(string? tags)
    {
        return string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static WorkItemType ParseWorkItemType(string? workItemType)
    {
        return workItemType?.Trim() switch
        {
            "User Story" => WorkItemType.UserStory,
            "Bug" => WorkItemType.Bug,
            "Task" => WorkItemType.Task,
            _ => WorkItemType.Unknown,
        };
    }

    private static double ParseStackRank(JsonElement rankElement)
    {
        return rankElement.ValueKind switch
        {
            JsonValueKind.Number when rankElement.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(rankElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => double.MaxValue,
        };
    }

    private static int? ParsePriority(JsonElement priorityElement)
    {
        return priorityElement.ValueKind switch
        {
            JsonValueKind.Number when priorityElement.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(priorityElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }
}
