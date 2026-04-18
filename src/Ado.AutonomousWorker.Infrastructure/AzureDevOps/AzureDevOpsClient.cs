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
    ILogger<AzureDevOpsClient> logger
) : IAzureDevOpsClient
{
    private const string ApiVersion = "7.1";

    private static string? _cachedSprintPath;

    private readonly HttpClient _httpClient = httpClient;
    private readonly AzureDevOpsOptions _options = options.Value;
    private readonly RepositoryOptions _repositoryOptions = repositoryOptions.Value;
    private readonly ILogger<AzureDevOpsClient> _logger = logger;

    public async Task<string?> GetCurrentSprintPathAsync(CancellationToken cancellationToken)
    {
        if (_cachedSprintPath is not null)
            return _cachedSprintPath;

        var team = Uri.EscapeDataString(_options.Team);
        var response = await _httpClient.GetAsync(
            $"{_options.Project}/{team}/_apis/work/teamsettings/iterations?$timeframe=current&api-version={ApiVersion}",
            cancellationToken
        );
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AdoIterationsResponse>(cancellationToken);
        _cachedSprintPath = result
            ?.Value.FirstOrDefault(i =>
                string.Equals(i.Attributes?.TimeFrame, "current", StringComparison.OrdinalIgnoreCase)
            )
            ?.Path;

        return _cachedSprintPath;
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
            cancellationToken
        );
        response.EnsureSuccessStatusCode();

        var workItem =
            await response.Content.ReadFromJsonAsync<AdoWorkItemResponse>(cancellationToken)
            ?? throw new InvalidOperationException($"Azure DevOps returned null for work item {workItemId}.");

        var fields = workItem.Fields;
        var tags = ParseTags(fields.Tags);
        var childIds = new List<int>();
        var parentIds = new List<int>();

        foreach (var relation in workItem.Relations ?? [])
        {
            if (relation.Rel is null || relation.Url is null)
                continue;
            var relatedId = TryParseWorkItemId(relation.Url);
            if (relatedId is null)
                continue;

            if (string.Equals(relation.Rel, "System.LinkTypes.Hierarchy-Forward", StringComparison.OrdinalIgnoreCase))
                childIds.Add(relatedId.Value);
            else if (
                string.Equals(relation.Rel, "System.LinkTypes.Hierarchy-Reverse", StringComparison.OrdinalIgnoreCase)
            )
                parentIds.Add(relatedId.Value);
        }

        var childrenTask =
            childIds.Count == 0
                ? Task.FromResult<IReadOnlyList<ChildTaskRecord>>([])
                : GetChildTasksAsync(childIds, cancellationToken);
        var parentFeatureTask =
            parentIds.Count == 0
                ? Task.FromResult<string?>(null)
                : TryGetParentFeatureTitleAsync(parentIds, cancellationToken);
        var commentsTask = GetWorkItemCommentsAsync(workItemId, cancellationToken);

        try
        {
            await Task.WhenAll(childrenTask, parentFeatureTask, commentsTask);
        }
        catch
        {
            if (childrenTask.IsFaulted)
                _logger.LogError(
                    childrenTask.Exception,
                    "Failed to fetch child tasks for work item {WorkItemId}",
                    workItemId
                );
            if (parentFeatureTask.IsFaulted)
                _logger.LogError(
                    parentFeatureTask.Exception,
                    "Failed to fetch parent feature for work item {WorkItemId}",
                    workItemId
                );
            if (commentsTask.IsFaulted)
                _logger.LogError(
                    commentsTask.Exception,
                    "Failed to fetch comments for work item {WorkItemId}",
                    workItemId
                );
            throw;
        }

        var children = await childrenTask;
        var parentFeatureTitle = await parentFeatureTask;
        var comments = await commentsTask;

        return new WorkItemDetails
        {
            WorkItemId = workItem.Id,
            WorkItemType = ParseWorkItemType(fields.WorkItemType),
            Title = fields.Title ?? string.Empty,
            State = fields.State ?? string.Empty,
            AssignedTo = ReadIdentityString(fields.AssignedTo),
            IterationPath = fields.IterationPath ?? string.Empty,
            Description = fields.Description ?? string.Empty,
            AcceptanceCriteria = fields.AcceptanceCriteria ?? string.Empty,
            ParentFeatureTitle = parentFeatureTitle,
            Comments = comments,
            Tags = tags,
            ChildTasks = children,
            Kind = tags.Contains("AI-Rework", StringComparer.OrdinalIgnoreCase)
                ? QueueItemKind.AiRework
                : QueueItemKind.AiWork,
        };
    }

    public Task UpdateWorkItemStateAsync(int workItemId, string state, CancellationToken cancellationToken) =>
        PatchFieldsAsync(workItemId, [PatchField("/fields/System.State", state)], cancellationToken);

    public Task AssignWorkItemAsync(int workItemId, string assignee, CancellationToken cancellationToken) =>
        PatchFieldsAsync(workItemId, [PatchField("/fields/System.AssignedTo", assignee)], cancellationToken);

    public Task UpdateIterationPathAsync(int workItemId, string iterationPath, CancellationToken cancellationToken) =>
        PatchFieldsAsync(workItemId, [PatchField("/fields/System.IterationPath", iterationPath)], cancellationToken);

    public Task UpdateTagsAsync(
        int workItemId,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken
    ) => PatchFieldsAsync(workItemId, [PatchField("/fields/System.Tags", string.Join("; ", tags))], cancellationToken);

    public async Task<int> CreateChildTaskAsync(
        int parentWorkItemId,
        string title,
        IReadOnlyCollection<string> tags,
        string iterationPath,
        string assignee,
        CancellationToken cancellationToken
    )
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
            createDocument
        );

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var created =
            await response.Content.ReadFromJsonAsync<AdoCreatedItemResponse>(cancellationToken)
            ?? throw new InvalidOperationException(
                "Azure DevOps did not return a work item ID for the new child task."
            );
        var childTaskId = created.Id;

        // ADO processes JSON Patch operations in document order: state transitions before relation validation.
        await PatchFieldsAsync(
            childTaskId,
            [PatchField("/fields/System.State", "Active"), PatchRelation(parentWorkItemId)],
            cancellationToken
        );
        return childTaskId;
    }

    public async Task<PullRequestInfo> CreateDraftPullRequestAsync(
        string repositoryName,
        string sourceBranch,
        string targetBranch,
        string title,
        string description,
        CancellationToken cancellationToken
    )
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
            cancellationToken
        );

        response.EnsureSuccessStatusCode();

        var pr =
            await response.Content.ReadFromJsonAsync<AdoPullRequest>(cancellationToken)
            ?? throw new InvalidOperationException("Azure DevOps did not return a pull request.");
        var prId = pr.PullRequestId;
        var url = new Uri(
            pr.Url ?? throw new InvalidOperationException("Azure DevOps did not return a pull request URL.")
        );
        var projectId = pr.Repository?.Project?.Id;
        var repoId = pr.Repository?.Id;
        var artifactUri =
            projectId is null || repoId is null ? null : $"vstfs:///Git/PullRequestId/{projectId}%2f{repoId}%2f{prId}";

        await AddPullRequestLabelAsync(repositoryName, prId, "AI-Generated", cancellationToken);
        return new PullRequestInfo(prId, title, url, artifactUri);
    }

    public async Task LinkPullRequestAsync(
        int workItemId,
        PullRequestInfo pullRequest,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(pullRequest.ArtifactUri))
        {
            await AddCommentAsync(workItemId, $"Draft PR: {pullRequest.Url}", cancellationToken);
            return;
        }

        await PatchFieldsAsync(
            workItemId,
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
            ],
            cancellationToken
        );
    }

    public async Task AddCommentAsync(int workItemId, string comment, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.Project}/_apis/wit/workItems/{workItemId}/comments?format=Markdown&api-version=7.2-preview.4",
            new { text = comment },
            cancellationToken
        );
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<QueueItem>> QueryAiPrFixAsync(CancellationToken cancellationToken)
    {
        var url =
            $"{_options.Project}/_apis/git/pullrequests?searchCriteria.status=active&searchCriteria.includeLinks=true&api-version={ApiVersion}";
        _logger.LogDebug("QueryAiPrFixAsync: GET {Url}", url);

        var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AdoPullRequestListResponse>(cancellationToken);
        var prList = result?.Value ?? [];
        _logger.LogDebug("QueryAiPrFixAsync: {Count} active PRs returned", prList.Count);

        var results = new List<QueueItem>();
        foreach (var pr in prList)
        {
            var items = await ParsePrFixItemsAsync(pr, cancellationToken);
            results.AddRange(items);
        }

        _logger.LogDebug("QueryAiPrFixAsync: {Count} PR fix items found", results.Count);
        return results;
    }

    public async Task<QueueItem?> GetPrFixItemAsync(int pullRequestId, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"{_options.Project}/_apis/git/pullrequests?searchCriteria.pullRequestId={pullRequestId}&searchCriteria.includeLinks=true&api-version={ApiVersion}",
            cancellationToken
        );
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AdoPullRequestListResponse>(cancellationToken);
        foreach (var pr in result?.Value ?? [])
        {
            var items = await ParsePrFixItemsAsync(pr, cancellationToken);
            if (items.Count > 0)
                return items[0];
        }

        return null;
    }

    private async Task<IReadOnlyList<QueueItem>> ParsePrFixItemsAsync(
        AdoPullRequest pr,
        CancellationToken cancellationToken
    )
    {
        var prId = pr.PullRequestId;
        var hasAiRework = false;
        var labelNames = new List<string>();

        foreach (var label in pr.Labels ?? [])
        {
            var name = label.Name ?? string.Empty;
            labelNames.Add(name);
            if (string.Equals(name, "AI-Rework", StringComparison.OrdinalIgnoreCase))
                hasAiRework = true;
        }

        _logger.LogDebug(
            "PR #{PrId}: labels=[{Labels}] hasAiRework={HasAiRework}",
            prId,
            string.Join(", ", labelNames),
            hasAiRework
        );

        if (!hasAiRework)
        {
            _logger.LogDebug("PR #{PrId}: skipped (missing required AI-Rework label)", prId);
            return [];
        }

        var repositoryName = pr.Repository?.Name ?? string.Empty;
        var sourceBranch = pr.SourceRefName ?? string.Empty;
        var branchName = sourceBranch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? sourceBranch["refs/heads/".Length..]
            : sourceBranch;
        var scope = DeriveScope(repositoryName);

        var workItemIds = await GetPrWorkItemIdsAsync(repositoryName, prId, cancellationToken);
        _logger.LogDebug("PR #{PrId}: work items resolved to ids=[{Ids}]", prId, string.Join(", ", workItemIds));

        if (workItemIds.Length == 0)
        {
            _logger.LogDebug("PR #{PrId}: skipped (no linked work items)", prId);
            return [];
        }

        var summaries = await GetWorkItemSummariesAsync(workItemIds, cancellationToken);
        return summaries
            .Select(s => new QueueItem(
                s.WorkItemId,
                s.WorkItemType,
                s.Title,
                QueueItemKind.AiPrFix,
                s.StackRank,
                s.Priority,
                s.State,
                s.AssignedTo,
                scope,
                prId,
                repositoryName,
                branchName
            ))
            .ToArray();
    }

    public async Task<IReadOnlyList<PrReviewThread>> GetPullRequestThreadsAsync(
        string repositoryName,
        int pullRequestId,
        CancellationToken cancellationToken
    )
    {
        var response = await _httpClient.GetAsync(
            $"{_options.Project}/_apis/git/repositories/{repositoryName}/pullrequests/{pullRequestId}/threads?api-version={ApiVersion}",
            cancellationToken
        );
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AdoPrThreadListResponse>(cancellationToken);
        var threads = new List<PrReviewThread>();

        foreach (var thread in result?.Value ?? [])
        {
            var comments = new List<PrReviewComment>();
            foreach (var comment in thread.Comments ?? [])
            {
                if (string.Equals(comment.CommentType, "system", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrWhiteSpace(comment.Content))
                    comments.Add(
                        new PrReviewComment(comment.Id, comment.Author?.DisplayName ?? string.Empty, comment.Content)
                    );
            }

            if (comments.Count > 0)
                threads.Add(
                    new PrReviewThread(
                        thread.Id,
                        thread.ThreadContext?.FilePath,
                        thread.ThreadContext?.RightFileEnd?.Line,
                        thread.Status ?? "unknown",
                        comments
                    )
                );
        }

        return threads;
    }

    public async Task<(string RepositoryName, string BranchName)?> GetPullRequestLocationAsync(
        int pullRequestId,
        CancellationToken cancellationToken
    )
    {
        var response = await _httpClient.GetAsync(
            $"{_options.Project}/_apis/git/pullrequests?searchCriteria.pullRequestId={pullRequestId}&api-version={ApiVersion}",
            cancellationToken
        );
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AdoPullRequestListResponse>(cancellationToken);
        var pr = result?.Value?.FirstOrDefault();
        if (pr is null)
            return null;

        var repoName = pr.Repository?.Name ?? string.Empty;
        var sourceRef = pr.SourceRefName ?? string.Empty;
        var branch = sourceRef.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? sourceRef["refs/heads/".Length..]
            : sourceRef;
        return (repoName, branch);
    }

    public async Task RemovePullRequestLabelAsync(
        string repositoryName,
        int pullRequestId,
        string labelName,
        CancellationToken cancellationToken
    )
    {
        var encodedLabel = Uri.EscapeDataString(labelName);
        var response = await _httpClient.DeleteAsync(
            $"{_options.Project}/_apis/git/repositories/{repositoryName}/pullrequests/{pullRequestId}/labels/{encodedLabel}?api-version={ApiVersion}",
            cancellationToken
        );
        response.EnsureSuccessStatusCode();
    }

    private async Task<int[]> GetPrWorkItemIdsAsync(
        string repositoryName,
        int pullRequestId,
        CancellationToken cancellationToken
    )
    {
        var response = await _httpClient.GetAsync(
            $"{_options.Project}/_apis/git/repositories/{repositoryName}/pullrequests/{pullRequestId}/workitems?api-version={ApiVersion}",
            cancellationToken
        );
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AdoPrWorkItemsResponse>(cancellationToken);
        if (result?.Value is null)
            return [];

        return result
            .Value.Select(item =>
            {
                var idEl = item.Id;
                if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out var n))
                    return (int?)n;
                if (idEl.ValueKind == JsonValueKind.String && int.TryParse(idEl.GetString(), out var s))
                    return s;
                return null;
            })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToArray();
    }

    private WorkItemScope DeriveScope(string repositoryName)
    {
        if (
            string.Equals(
                _repositoryOptions.Backend.AzureDevOpsRepository,
                repositoryName,
                StringComparison.OrdinalIgnoreCase
            )
        )
            return WorkItemScope.Backend;
        if (
            string.Equals(
                _repositoryOptions.Frontend.AzureDevOpsRepository,
                repositoryName,
                StringComparison.OrdinalIgnoreCase
            )
        )
            return WorkItemScope.Frontend;
        return WorkItemScope.Fullstack;
    }

    private async Task<IReadOnlyList<WorkItemSummary>> QueryByWiqlAsync(
        string query,
        CancellationToken cancellationToken
    )
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.Project}/_apis/wit/wiql?api-version={ApiVersion}",
            new { query },
            cancellationToken
        );
        response.EnsureSuccessStatusCode();

        var wiql = await response.Content.ReadFromJsonAsync<AdoWiqlResponse>(cancellationToken);
        var ids = wiql?.WorkItems?.Select(w => w.Id).ToArray() ?? [];

        return ids.Length == 0 ? [] : await GetWorkItemSummariesAsync(ids, cancellationToken);
    }

    private async Task<IReadOnlyList<WorkItemSummary>> GetWorkItemSummariesAsync(
        IReadOnlyList<int> ids,
        CancellationToken cancellationToken
    )
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
            cancellationToken
        );

        response.EnsureSuccessStatusCode();

        var batch = await response.Content.ReadFromJsonAsync<AdoWorkItemBatchResponse>(cancellationToken);
        return (batch?.Value ?? [])
            .Select(item => new WorkItemSummary(
                item.Fields.Id,
                ParseWorkItemType(item.Fields.WorkItemType),
                item.Fields.Title ?? string.Empty,
                item.Fields.State ?? string.Empty,
                ReadIdentityString(item.Fields.AssignedTo),
                ParseStackRank(item.Fields.StackRank),
                ParsePriority(item.Fields.Priority),
                ParseTags(item.Fields.Tags)
            ))
            .ToList();
    }

    private async Task<IReadOnlyList<ChildTaskRecord>> GetChildTasksAsync(
        IReadOnlyList<int> ids,
        CancellationToken cancellationToken
    )
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.Project}/_apis/wit/workitemsbatch?api-version={ApiVersion}",
            new { ids, fields = new[] { "System.Id", "System.Title", "System.State", "System.Tags" } },
            cancellationToken
        );

        response.EnsureSuccessStatusCode();

        var batch = await response.Content.ReadFromJsonAsync<AdoWorkItemBatchResponse>(cancellationToken);
        return (batch?.Value ?? [])
            .Select(item =>
            {
                var title = item.Fields.Title ?? string.Empty;
                var tags = ParseTags(item.Fields.Tags);
                var repoKind =
                    tags.Contains("Frontend", StringComparer.OrdinalIgnoreCase)
                    || title.Contains("frontend", StringComparison.OrdinalIgnoreCase)
                        ? RepoKind.Frontend
                        : RepoKind.Backend;
                return new ChildTaskRecord(item.Fields.Id, repoKind, title, item.Fields.State ?? string.Empty);
            })
            .ToList();
    }

    private async Task<string?> TryGetParentFeatureTitleAsync(
        IReadOnlyList<int> parentIds,
        CancellationToken cancellationToken
    )
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.Project}/_apis/wit/workitemsbatch?api-version={ApiVersion}",
            new { ids = parentIds, fields = new[] { "System.WorkItemType", "System.Title" } },
            cancellationToken
        );

        response.EnsureSuccessStatusCode();

        var batch = await response.Content.ReadFromJsonAsync<AdoWorkItemBatchResponse>(cancellationToken);
        return (batch?.Value ?? [])
            .FirstOrDefault(item =>
                string.Equals(item.Fields.WorkItemType, "Feature", StringComparison.OrdinalIgnoreCase)
            )
            ?.Fields.Title;
    }

    private async Task<IReadOnlyList<string>> GetWorkItemCommentsAsync(
        int workItemId,
        CancellationToken cancellationToken
    )
    {
        var comments = new List<string>();
        string? continuationToken = null;

        do
        {
            var route =
                $"{_options.Project}/_apis/wit/workItems/{workItemId}/comments?$top=200&api-version=7.1-preview.3";
            if (!string.IsNullOrWhiteSpace(continuationToken))
                route = $"{route}&continuationToken={Uri.EscapeDataString(continuationToken)}";

            var response = await _httpClient.GetAsync(route, cancellationToken);
            response.EnsureSuccessStatusCode();

            var page = await response.Content.ReadFromJsonAsync<AdoWorkItemCommentsResponse>(cancellationToken);
            foreach (var comment in page?.Comments ?? [])
            {
                if (!string.IsNullOrWhiteSpace(comment.Text))
                    comments.Add(comment.Text.Trim());
            }

            continuationToken = page?.ContinuationToken;
        } while (!string.IsNullOrWhiteSpace(continuationToken));

        return comments;
    }

    private async Task PatchFieldsAsync(int workItemId, object[] document, CancellationToken cancellationToken)
    {
        using var request = BuildPatchRequest(
            $"{_options.Project}/_apis/wit/workitems/{workItemId}?api-version={ApiVersion}",
            document
        );

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        _logger.LogDebug("Patched work item {WorkItemId}", workItemId);
    }

    private static HttpRequestMessage BuildPatchRequest(string route, object[] document)
    {
        return new HttpRequestMessage(new HttpMethod("PATCH"), route)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(document),
                Encoding.UTF8,
                "application/json-patch+json"
            ),
        };
    }

    private async Task AddPullRequestLabelAsync(
        string repositoryName,
        int pullRequestId,
        string labelName,
        CancellationToken cancellationToken
    )
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.Project}/_apis/git/repositories/{repositoryName}/pullrequests/{pullRequestId}/labels?api-version={ApiVersion}",
            new { name = labelName },
            cancellationToken
        );

        response.EnsureSuccessStatusCode();
    }

    private static object PatchField(string path, object value) =>
        new
        {
            op = "add",
            path,
            value,
        };

    private object PatchRelation(int parentWorkItemId) =>
        new
        {
            op = "add",
            path = "/relations/-",
            value = new
            {
                rel = "System.LinkTypes.Hierarchy-Reverse",
                url = $"{_options.OrganizationUrl.TrimEnd('/')}/{_options.Project}/_apis/wit/workItems/{parentWorkItemId}",
            },
        };

    private static string? ReadIdentityString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Object when element.TryGetProperty("uniqueName", out var uniqueName) =>
                uniqueName.GetString(),
            JsonValueKind.Object when element.TryGetProperty("displayName", out var displayName) =>
                displayName.GetString(),
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
            JsonValueKind.String
                when double.TryParse(
                    rankElement.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsed
                ) => parsed,
            _ => double.MaxValue,
        };
    }

    private static int? ParsePriority(JsonElement priorityElement)
    {
        return priorityElement.ValueKind switch
        {
            JsonValueKind.Number when priorityElement.TryGetInt32(out var number) => number,
            JsonValueKind.String
                when int.TryParse(
                    priorityElement.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsed
                ) => parsed,
            _ => null,
        };
    }

    // ── Deploy / pipeline ─────────────────────────────────────────────────────

    public async Task<string?> TryGetPipelineNameAsync(int pipelineId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"{_options.Project}/_apis/pipelines/{pipelineId}?api-version={ApiVersion}",
                cancellationToken
            );

            if (!response.IsSuccessStatusCode)
                return null;

            var result = await response.Content.ReadFromJsonAsync<AdoPipelineResponse>(cancellationToken);
            return result?.Name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "TryGetPipelineNameAsync failed for pipeline {PipelineId}", pipelineId);
            return null;
        }
    }

    public async Task<int> TriggerPipelineAsync(int pipelineId, string refName, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.Project}/_apis/pipelines/{pipelineId}/runs?api-version={ApiVersion}",
            new { resources = new { repositories = new { self = new { refName } } } },
            cancellationToken
        );

        response.EnsureSuccessStatusCode();

        var run =
            await response.Content.ReadFromJsonAsync<AdoPipelineRunResponse>(cancellationToken)
            ?? throw new InvalidOperationException($"Azure DevOps did not return a run ID for pipeline {pipelineId}.");
        return run.Id;
    }

    public async Task<(string State, string? Result)> GetPipelineRunStatusAsync(
        int pipelineId,
        int runId,
        CancellationToken cancellationToken
    )
    {
        var response = await _httpClient.GetAsync(
            $"{_options.Project}/_apis/pipelines/{pipelineId}/runs/{runId}?api-version={ApiVersion}",
            cancellationToken
        );
        response.EnsureSuccessStatusCode();

        var run =
            await response.Content.ReadFromJsonAsync<AdoPipelineRunResponse>(cancellationToken)
            ?? throw new InvalidOperationException(
                $"Azure DevOps returned null for pipeline {pipelineId} run {runId}."
            );

        return (run.State ?? "unknown", run.Result);
    }

    public Task<IReadOnlyList<WorkItemSummary>> QueryResolvedSprintItemsAsync(CancellationToken cancellationToken)
    {
        var query = $"""
            SELECT [System.Id], [System.Title], [System.State], [System.AssignedTo], [Microsoft.VSTS.Common.StackRank], [Microsoft.VSTS.Common.Priority], [System.WorkItemType]
            FROM WorkItems
            WHERE ([System.WorkItemType] = 'Task' OR [System.WorkItemType] = 'Bug')
              AND [System.State] = 'Resolved'
              AND [System.IterationPath] = @CurrentIteration('[{_options.Project}]\{_options.Team}')
              AND [System.TeamProject] = '{_options.Project}'
            ORDER BY [Microsoft.VSTS.Common.StackRank] ASC
            """;

        return QueryByWiqlAsync(query, cancellationToken);
    }

    public async Task<
        IReadOnlyList<(WorkItemSummary Story, IReadOnlyList<int> ChildIds)>
    > QueryActiveSprintUserStoriesWithChildrenAsync(CancellationToken cancellationToken)
    {
        var query = $"""
            SELECT [System.Id], [System.Title], [System.State], [System.AssignedTo], [Microsoft.VSTS.Common.StackRank], [Microsoft.VSTS.Common.Priority], [System.WorkItemType]
            FROM WorkItems
            WHERE [System.WorkItemType] = 'User Story'
              AND [System.State] = 'Active'
              AND [System.IterationPath] = @CurrentIteration('[{_options.Project}]\{_options.Team}')
              AND [System.TeamProject] = '{_options.Project}'
            ORDER BY [Microsoft.VSTS.Common.StackRank] ASC
            """;

        var stories = await QueryByWiqlAsync(query, cancellationToken);
        if (stories.Count == 0)
            return [];

        var ids = stories.Select(s => s.WorkItemId).ToArray();
        var childMap = await GetChildWorkItemIdsAsync(ids, cancellationToken);

        return stories
            .Select(s => (s, childMap.TryGetValue(s.WorkItemId, out var children) ? children : (IReadOnlyList<int>)[]))
            .ToArray();
    }

    public async Task<IReadOnlyDictionary<int, string>> GetWorkItemStatesAsync(
        IReadOnlyList<int> workItemIds,
        CancellationToken cancellationToken
    )
    {
        if (workItemIds.Count == 0)
            return new Dictionary<int, string>();

        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.Project}/_apis/wit/workitemsbatch?api-version={ApiVersion}",
            new { ids = workItemIds, fields = new[] { "System.Id", "System.State" } },
            cancellationToken
        );

        response.EnsureSuccessStatusCode();

        var batch = await response.Content.ReadFromJsonAsync<AdoWorkItemBatchResponse>(cancellationToken);
        return (batch?.Value ?? []).ToDictionary(item => item.Fields.Id, item => item.Fields.State ?? string.Empty);
    }

    public Task UpdateWorkItemStateAndAssigneeAsync(
        int workItemId,
        string state,
        string assignee,
        CancellationToken cancellationToken
    ) =>
        PatchFieldsAsync(
            workItemId,
            [PatchField("/fields/System.State", state), PatchField("/fields/System.AssignedTo", assignee)],
            cancellationToken
        );

    private async Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetChildWorkItemIdsAsync(
        IReadOnlyList<int> parentIds,
        CancellationToken cancellationToken
    )
    {
        if (parentIds.Count == 0)
            return new Dictionary<int, IReadOnlyList<int>>();

        var idList = string.Join(", ", parentIds);
        var query = $"""
            SELECT [System.Id]
            FROM WorkItemLinks
            WHERE [Source].[System.Id] IN ({idList})
              AND [System.Links.LinkType] = 'System.LinkTypes.Hierarchy-Forward'
              AND [Target].[System.TeamProject] = '{_options.Project}'
            MODE (MayContain)
            """;

        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.Project}/_apis/wit/wiql?api-version={ApiVersion}",
            new { query },
            cancellationToken
        );
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AdoWiqlTreeResponse>(cancellationToken);

        return (result?.WorkItemRelations ?? [])
            .Where(r => r.Source is not null && r.Target is not null)
            .GroupBy(r => r.Source!.Id, r => r.Target!.Id)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.ToList());
    }
}
