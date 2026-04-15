using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.AzureDevOps;

public sealed class AzureDevOpsClient(
    HttpClient httpClient,
    IOptions<AzureDevOpsOptions> options,
    IOptions<RepositoryOptions> repositoryOptions,
    ILogger<AzureDevOpsClient> logger) : IAzureDevOpsClient
{
    private const string ApiVersion = "7.1";

    private readonly HttpClient _httpClient = httpClient;
    private readonly AzureDevOpsOptions _options = options.Value;
    private readonly RepositoryOptions _repositoryOptions = repositoryOptions.Value;
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
        var parentIds = new List<int>();

        if (root.TryGetProperty("relations", out var relationsElement))
        {
            foreach (var relation in relationsElement.EnumerateArray())
            {
                if (!relation.TryGetProperty("rel", out var relElement))
                {
                    continue;
                }

                if (!relation.TryGetProperty("url", out var urlElement))
                {
                    continue;
                }

                var relatedId = TryParseWorkItemId(urlElement.GetString());
                if (relatedId is null)
                {
                    continue;
                }

                var relationType = relElement.GetString();
                if (string.Equals(relationType, "System.LinkTypes.Hierarchy-Forward", StringComparison.OrdinalIgnoreCase))
                {
                    childIds.Add(relatedId.Value);
                }
                else if (string.Equals(relationType, "System.LinkTypes.Hierarchy-Reverse", StringComparison.OrdinalIgnoreCase))
                {
                    parentIds.Add(relatedId.Value);
                }
            }
        }

        var children = childIds.Count == 0
            ? []
            : await GetChildTasksAsync(childIds, cancellationToken);
        var parentFeatureTitle = parentIds.Count == 0
            ? null
            : await TryGetParentFeatureTitleAsync(parentIds, cancellationToken);
        var comments = await GetWorkItemCommentsAsync(workItemId, cancellationToken);

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
            ParentFeatureTitle = parentFeatureTitle,
            Comments = comments,
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

        await AddPullRequestLabelAsync(repositoryName, prId, "AI-Generated", cancellationToken);
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
            $"{_options.Project}/_apis/wit/workItems/{workItemId}/comments?format=Markdown&api-version=7.2-preview.4",
            new { text = comment },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<QueueItem>> QueryAiPrFixAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"{_options.Project}/_apis/git/pullrequests?searchCriteria.labels=AI-Rework&searchCriteria.status=active&api-version={ApiVersion}",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("value", out var prArray))
            return [];

        var results = new List<QueueItem>();
        foreach (var pr in prArray.EnumerateArray())
        {
            // Only process PRs that also have the "AI-Generated" label
            var hasAiGenerated = false;
            if (pr.TryGetProperty("labels", out var labelsElement))
            {
                foreach (var label in labelsElement.EnumerateArray())
                {
                    if (label.TryGetProperty("name", out var labelName) &&
                        string.Equals(labelName.GetString(), "AI-Generated", StringComparison.OrdinalIgnoreCase))
                    {
                        hasAiGenerated = true;
                        break;
                    }
                }
            }

            if (!hasAiGenerated)
                continue;

            var pullRequestId = pr.GetProperty("pullRequestId").GetInt32();
            var repositoryName = pr.GetProperty("repository").GetProperty("name").GetString() ?? string.Empty;
            var sourceBranch = pr.GetProperty("sourceRefName").GetString() ?? string.Empty;
            // Strip "refs/heads/" prefix
            var branchName = sourceBranch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
                ? sourceBranch["refs/heads/".Length..]
                : sourceBranch;

            var scope = DeriveScope(repositoryName);

            // Extract linked work item IDs
            if (!pr.TryGetProperty("workItemRefs", out var workItemRefs))
                continue;

            var workItemIds = workItemRefs.EnumerateArray()
                .Select(r => r.TryGetProperty("id", out var idEl) && int.TryParse(idEl.GetString(), out var wid) ? (int?)wid : null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToArray();

            if (workItemIds.Length == 0)
                continue;

            var summaries = await GetWorkItemSummariesAsync(workItemIds, cancellationToken);
            foreach (var summary in summaries)
            {
                results.Add(new QueueItem(
                    summary.WorkItemId,
                    summary.WorkItemType,
                    summary.Title,
                    QueueItemKind.AiPrFix,
                    summary.StackRank,
                    summary.Priority,
                    summary.State,
                    summary.AssignedTo,
                    scope,
                    pullRequestId,
                    repositoryName,
                    branchName));
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<PrReviewThread>> GetPullRequestThreadsAsync(string repositoryName, int pullRequestId, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"{_options.Project}/_apis/git/repositories/{repositoryName}/pullrequests/{pullRequestId}/threads?api-version={ApiVersion}",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("value", out var threadsArray))
            return [];

        var threads = new List<PrReviewThread>();
        foreach (var thread in threadsArray.EnumerateArray())
        {
            var threadId = thread.TryGetProperty("id", out var threadIdEl) ? threadIdEl.GetInt32() : 0;
            var status = thread.TryGetProperty("status", out var statusEl) ? statusEl.GetString() ?? "unknown" : "unknown";

            string? filePath = null;
            int? lineNumber = null;
            if (thread.TryGetProperty("threadContext", out var threadContext) && threadContext.ValueKind != JsonValueKind.Null)
            {
                filePath = threadContext.TryGetProperty("filePath", out var filePathEl) ? filePathEl.GetString() : null;
                if (threadContext.TryGetProperty("rightFileEnd", out var rightFileEnd) &&
                    rightFileEnd.ValueKind != JsonValueKind.Null &&
                    rightFileEnd.TryGetProperty("line", out var lineEl))
                {
                    lineNumber = lineEl.GetInt32();
                }
            }

            if (!thread.TryGetProperty("comments", out var commentsArray))
                continue;

            var comments = new List<PrReviewComment>();
            foreach (var comment in commentsArray.EnumerateArray())
            {
                // Skip system-generated comments
                if (comment.TryGetProperty("commentType", out var commentTypeEl) &&
                    string.Equals(commentTypeEl.GetString(), "system", StringComparison.OrdinalIgnoreCase))
                    continue;

                var commentId = comment.TryGetProperty("id", out var commentIdEl) ? commentIdEl.GetInt32() : 0;
                var content = comment.TryGetProperty("content", out var contentEl) ? contentEl.GetString() ?? string.Empty : string.Empty;
                var author = string.Empty;
                if (comment.TryGetProperty("author", out var authorEl))
                {
                    author = authorEl.TryGetProperty("displayName", out var displayNameEl)
                        ? displayNameEl.GetString() ?? string.Empty
                        : string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(content))
                    comments.Add(new PrReviewComment(commentId, author, content));
            }

            if (comments.Count > 0)
                threads.Add(new PrReviewThread(threadId, filePath, lineNumber, status, comments));
        }

        return threads;
    }

    public async Task<(string RepositoryName, string BranchName)?> GetPullRequestLocationAsync(int pullRequestId, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"{_options.Project}/_apis/git/pullrequests?searchCriteria.pullRequestId={pullRequestId}&api-version={ApiVersion}",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("value", out var valueArray))
            return null;

        foreach (var pr in valueArray.EnumerateArray())
        {
            var repoName = pr.GetProperty("repository").GetProperty("name").GetString() ?? string.Empty;
            var sourceRef = pr.GetProperty("sourceRefName").GetString() ?? string.Empty;
            var branch = sourceRef.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
                ? sourceRef["refs/heads/".Length..]
                : sourceRef;
            return (repoName, branch);
        }

        return null;
    }

    public async Task RemovePullRequestLabelAsync(string repositoryName, int pullRequestId, string labelName, CancellationToken cancellationToken)
    {
        var encodedLabel = Uri.EscapeDataString(labelName);
        var response = await _httpClient.DeleteAsync(
            $"{_options.Project}/_apis/git/repositories/{repositoryName}/pullrequests/{pullRequestId}/labels/{encodedLabel}?api-version={ApiVersion}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private WorkItemScope DeriveScope(string repositoryName)
    {
        if (string.Equals(_repositoryOptions.Backend.AzureDevOpsRepository, repositoryName, StringComparison.OrdinalIgnoreCase))
            return WorkItemScope.Backend;
        if (string.Equals(_repositoryOptions.Frontend.AzureDevOpsRepository, repositoryName, StringComparison.OrdinalIgnoreCase))
            return WorkItemScope.Frontend;
        return WorkItemScope.Fullstack;
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
                    "System.Tags",
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
                fields.TryGetProperty("Microsoft.VSTS.Common.Priority", out var priorityElement) ? ParsePriority(priorityElement) : null,
                ParseTags(fields.TryGetProperty("System.Tags", out var tagsElement) ? tagsElement.GetString() : null)));
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

    private async Task<string?> TryGetParentFeatureTitleAsync(IReadOnlyList<int> parentIds, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.Project}/_apis/wit/workitemsbatch?api-version={ApiVersion}",
            new
            {
                ids = parentIds,
                fields = new[] { "System.WorkItemType", "System.Title" },
            },
            cancellationToken);

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        foreach (var item in document.RootElement.GetProperty("value").EnumerateArray())
        {
            var fields = item.GetProperty("fields");
            if (!string.Equals(
                    fields.TryGetProperty("System.WorkItemType", out var typeElement) ? typeElement.GetString() : null,
                    "Feature",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return fields.TryGetProperty("System.Title", out var titleElement)
                ? titleElement.GetString()
                : null;
        }

        return null;
    }

    private async Task<IReadOnlyList<string>> GetWorkItemCommentsAsync(int workItemId, CancellationToken cancellationToken)
    {
        var comments = new List<string>();
        string? continuationToken = null;

        do
        {
            var route = $"{_options.Project}/_apis/wit/workItems/{workItemId}/comments?$top=200&api-version=7.1-preview.3";
            if (!string.IsNullOrWhiteSpace(continuationToken))
            {
                route = $"{route}&continuationToken={Uri.EscapeDataString(continuationToken)}";
            }

            var response = await _httpClient.GetAsync(route, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.TryGetProperty("comments", out var commentsElement))
            {
                foreach (var commentElement in commentsElement.EnumerateArray())
                {
                    if (!commentElement.TryGetProperty("text", out var textElement))
                    {
                        continue;
                    }

                    var text = textElement.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        comments.Add(text.Trim());
                    }
                }
            }

            continuationToken = document.RootElement.TryGetProperty("continuationToken", out var continuationElement)
                ? continuationElement.GetString()
                : null;
        }
        while (!string.IsNullOrWhiteSpace(continuationToken));

        return comments;
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

    private async Task AddPullRequestLabelAsync(string repositoryName, int pullRequestId, string labelName, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.Project}/_apis/git/repositories/{repositoryName}/pullrequests/{pullRequestId}/labels?api-version={ApiVersion}",
            new { name = labelName },
            cancellationToken);

        response.EnsureSuccessStatusCode();
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

    private static int? TryParseWorkItemId(string? url)
    {
        var segment = url?.Split('/').LastOrDefault();
        return int.TryParse(segment, out var id) ? id : null;
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
