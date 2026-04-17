using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ado.AutonomousWorker.Infrastructure.AzureDevOps;

internal sealed record AdoIterationsResponse([property: JsonPropertyName("value")] IReadOnlyList<AdoIteration> Value);

internal sealed record AdoIteration(
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("attributes")] AdoIterationAttributes? Attributes
);

internal sealed record AdoIterationAttributes([property: JsonPropertyName("timeFrame")] string? TimeFrame);

internal sealed record AdoWorkItemResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("fields")] AdoWorkItemFields Fields,
    [property: JsonPropertyName("relations")] IReadOnlyList<AdoWorkItemRelation>? Relations
);

internal sealed record AdoWorkItemFields(
    [property: JsonPropertyName("System.WorkItemType")] string? WorkItemType,
    [property: JsonPropertyName("System.Title")] string? Title,
    [property: JsonPropertyName("System.State")] string? State,
    [property: JsonPropertyName("System.AssignedTo")] JsonElement AssignedTo,
    [property: JsonPropertyName("System.IterationPath")] string? IterationPath,
    [property: JsonPropertyName("System.Description")] string? Description,
    [property: JsonPropertyName("Microsoft.VSTS.Common.AcceptanceCriteria")] string? AcceptanceCriteria,
    [property: JsonPropertyName("System.Tags")] string? Tags
);

internal sealed record AdoWorkItemRelation(
    [property: JsonPropertyName("rel")] string? Rel,
    [property: JsonPropertyName("url")] string? Url
);

internal sealed record AdoCreatedItemResponse([property: JsonPropertyName("id")] int Id);

internal sealed record AdoPullRequestListResponse(
    [property: JsonPropertyName("value")] IReadOnlyList<AdoPullRequest>? Value
);

internal sealed record AdoPullRequest(
    [property: JsonPropertyName("pullRequestId")] int PullRequestId,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("sourceRefName")] string? SourceRefName,
    [property: JsonPropertyName("repository")] AdoPrRepository? Repository,
    [property: JsonPropertyName("labels")] IReadOnlyList<AdoPrLabel>? Labels
);

internal sealed record AdoPrRepository(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("project")] AdoPrProject? Project
);

internal sealed record AdoPrProject([property: JsonPropertyName("id")] string? Id);

internal sealed record AdoPrLabel([property: JsonPropertyName("name")] string? Name);

internal sealed record AdoPrThreadListResponse([property: JsonPropertyName("value")] IReadOnlyList<AdoPrThread>? Value);

internal sealed record AdoPrThread(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("threadContext")] AdoPrThreadContext? ThreadContext,
    [property: JsonPropertyName("comments")] IReadOnlyList<AdoPrComment>? Comments
);

internal sealed record AdoPrThreadContext(
    [property: JsonPropertyName("filePath")] string? FilePath,
    [property: JsonPropertyName("rightFileEnd")] AdoPrFilePosition? RightFileEnd
);

internal sealed record AdoPrFilePosition([property: JsonPropertyName("line")] int Line);

internal sealed record AdoPrComment(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("commentType")] string? CommentType,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("author")] AdoPrAuthor? Author
);

internal sealed record AdoPrAuthor([property: JsonPropertyName("displayName")] string? DisplayName);

internal sealed record AdoPrWorkItemsResponse(
    [property: JsonPropertyName("value")] IReadOnlyList<AdoPrWorkItemRef>? Value
);

// ADO returns work item IDs as either number or string depending on context
internal sealed record AdoPrWorkItemRef([property: JsonPropertyName("id")] JsonElement Id);

internal sealed record AdoWiqlResponse(
    [property: JsonPropertyName("workItems")] IReadOnlyList<AdoWorkItemRef>? WorkItems
);

internal sealed record AdoWorkItemRef([property: JsonPropertyName("id")] int Id);

internal sealed record AdoWorkItemBatchResponse(
    [property: JsonPropertyName("value")] IReadOnlyList<AdoBatchWorkItem>? Value
);

internal sealed record AdoBatchWorkItem([property: JsonPropertyName("fields")] AdoBatchWorkItemFields Fields);

// AssignedTo is polymorphic (string or identity object); StackRank and Priority can be number or string
internal sealed record AdoBatchWorkItemFields(
    [property: JsonPropertyName("System.Id")] int Id,
    [property: JsonPropertyName("System.WorkItemType")] string? WorkItemType,
    [property: JsonPropertyName("System.Title")] string? Title,
    [property: JsonPropertyName("System.State")] string? State,
    [property: JsonPropertyName("System.AssignedTo")] JsonElement AssignedTo,
    [property: JsonPropertyName("Microsoft.VSTS.Common.StackRank")] JsonElement StackRank,
    [property: JsonPropertyName("Microsoft.VSTS.Common.Priority")] JsonElement Priority,
    [property: JsonPropertyName("System.Tags")] string? Tags
);

internal sealed record AdoWorkItemCommentsResponse(
    [property: JsonPropertyName("comments")] IReadOnlyList<AdoWorkItemCommentItem>? Comments,
    [property: JsonPropertyName("continuationToken")] string? ContinuationToken
);

internal sealed record AdoWorkItemCommentItem([property: JsonPropertyName("text")] string? Text);

// Pipeline / release API responses
internal sealed record AdoPipelineRunResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("url")] string? Url
);

internal sealed record AdoPipelineResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("url")] string? Url
);

internal sealed record AdoSprintWorkItemsResponse(
    [property: JsonPropertyName("workItems")] IReadOnlyList<AdoWorkItemRef>? WorkItems
);

internal sealed record AdoWiqlTreeResponse(
    [property: JsonPropertyName("workItemRelations")] IReadOnlyList<AdoWorkItemLinkRelation>? WorkItemRelations
);

internal sealed record AdoWorkItemLinkRelation(
    [property: JsonPropertyName("source")] AdoWorkItemRef? Source,
    [property: JsonPropertyName("target")] AdoWorkItemRef? Target
);

internal sealed record AdoWorkItemRelationsResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("fields")] AdoWorkItemBaseFields Fields,
    [property: JsonPropertyName("relations")] IReadOnlyList<AdoWorkItemRelation>? Relations
);

internal sealed record AdoWorkItemBaseFields(
    [property: JsonPropertyName("System.WorkItemType")] string? WorkItemType,
    [property: JsonPropertyName("System.Title")] string? Title,
    [property: JsonPropertyName("System.State")] string? State
);
