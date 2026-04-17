using System.Globalization;
using System.Text.Json;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Infrastructure.State.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ado.AutonomousWorker.Infrastructure.State;

internal sealed class SqliteStateStore(IDbContextFactory<WorkerDbContext> contextFactory) : IStateStore
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
    }

    public async Task SaveExecutionAsync(TaskExecutionRecord execution, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.Executions.FindAsync([execution.ExecutionId], cancellationToken);
        if (existing is null)
        {
            context.Executions.Add(ToEntity(execution));
        }
        else
        {
            existing.WorkItemId = execution.WorkItemId;
            existing.Title = execution.Title;
            existing.Kind = (int)execution.Kind;
            existing.Phase = (int)execution.Phase;
            existing.Status = (int)execution.Status;
            existing.UpdatedAt = execution.UpdatedAt;
            existing.PlanProviderSessionId = execution.ProviderPlanSessionId;
        }
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<TaskExecutionRecord?> GetExecutionAsync(string executionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context
            .Executions.AsNoTracking()
            .FirstOrDefaultAsync(e => e.ExecutionId == executionId, cancellationToken);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<IReadOnlyList<TaskExecutionRecord>> ListExecutionsAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .Executions.AsNoTracking()
            .OrderByDescending(e => e.UpdatedAt)
            .Select(e => ToDomain(e))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TaskExecutionRecord>> ListActiveExecutionsAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .Executions.AsNoTracking()
            .Where(e => e.Status == (int)ExecutionStatus.Running)
            .OrderByDescending(e => e.UpdatedAt)
            .Select(e => ToDomain(e))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TaskExecutionRecord>> ListExecutionsByWorkItemAsync(
        int workItemId,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .Executions.AsNoTracking()
            .Where(e => e.WorkItemId == workItemId)
            .OrderByDescending(e => e.UpdatedAt)
            .Select(e => ToDomain(e))
            .ToListAsync(cancellationToken);
    }

    public async Task UpdatePhaseAsync(string executionId, TaskPhase phase, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context
            .Executions.Where(e => e.ExecutionId == executionId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.Phase, (int)phase).SetProperty(e => e.UpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken
            );
    }

    public async Task UpdateStatusAsync(string executionId, ExecutionStatus status, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context
            .Executions.Where(e => e.ExecutionId == executionId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.Status, (int)status).SetProperty(e => e.UpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken
            );
    }

    public async Task AppendEventAsync(ExecutionEventRecord executionEvent, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.Events.Add(
            new ExecutionEventEntity
            {
                ExecutionId = executionEvent.ExecutionId,
                Timestamp = executionEvent.Timestamp,
                Phase = (int)executionEvent.Phase,
                Message = executionEvent.Message,
            }
        );
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAgentSessionAsync(
        string executionId,
        AgentSessionHandle session,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.Sessions.Add(
            new AgentSessionEntity
            {
                ExecutionId = executionId,
                SessionId = session.SessionId,
                Provider = (int)session.Provider,
                Role = (int)session.Role,
                WorkDir = session.WorkingDirectory,
                TranscriptPath = session.TranscriptPath,
                ProviderSessionId = session.ProviderSessionId,
            }
        );
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentSessionHandle>> GetAgentSessionsAsync(
        string executionId,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .Sessions.AsNoTracking()
            .Where(s => s.ExecutionId == executionId)
            .OrderBy(s => s.Id)
            .Select(s => new AgentSessionHandle(
                s.SessionId,
                (AgentProvider)s.Provider,
                (AgentRole)s.Role,
                s.WorkDir,
                s.TranscriptPath,
                s.ProviderSessionId
            ))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ExecutionEventRecord>> GetEventsAsync(
        string executionId,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .Events.AsNoTracking()
            .Where(e => e.ExecutionId == executionId)
            .OrderBy(e => e.Id)
            .Select(e => new ExecutionEventRecord(e.ExecutionId, e.Timestamp, (TaskPhase)e.Phase, e.Message))
            .ToListAsync(cancellationToken);
    }

    public async Task AppendAgentOutputChunkAsync(
        string executionId,
        AgentOutputChunk chunk,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.AgentOutputChunks.Add(
            new AgentOutputChunkEntity
            {
                ExecutionId = executionId,
                SessionId = chunk.SessionId,
                Provider = (int)chunk.Provider,
                Role = (int)chunk.Role,
                WorkDir = chunk.WorkingDirectory,
                Stream = (int)chunk.Stream,
                Sequence = chunk.Sequence,
                Timestamp = chunk.Timestamp,
                Content = Truncate(chunk.Content, 500),
            }
        );
        await context.SaveChangesAsync(cancellationToken);

        // Keep only the 200 most recent chunks per session.
        await context.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM agent_output_chunks WHERE id IN (
                SELECT id FROM agent_output_chunks
                WHERE execution_id = {executionId} AND session_id = {chunk.SessionId}
                ORDER BY sequence DESC LIMIT -1 OFFSET 200)
            """,
            cancellationToken
        );
    }

    public async Task<IReadOnlyList<AgentOutputChunk>> GetAgentOutputChunksAsync(
        string executionId,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .AgentOutputChunks.AsNoTracking()
            .Where(c => c.ExecutionId == executionId)
            .OrderBy(c => c.Timestamp)
            .ThenBy(c => c.Id)
            .Select(c => new AgentOutputChunk(
                c.SessionId,
                (AgentProvider)c.Provider,
                (AgentRole)c.Role,
                c.WorkDir,
                (AgentOutputStream)c.Stream,
                c.Sequence,
                c.Timestamp,
                c.Content
            ))
            .ToListAsync(cancellationToken);
    }

    public async Task SavePlanAsync(string executionId, PlanResult planResult, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.ExecutionPlans.FindAsync([executionId], cancellationToken);
        if (existing is null)
        {
            context.ExecutionPlans.Add(
                new ExecutionPlanEntity
                {
                    ExecutionId = executionId,
                    Scope = (int)planResult.Scope,
                    RepositoriesJson = Serialize(planResult.Repositories),
                    ValidationTargetsJson = Serialize(planResult.ValidationTargets),
                    TestIntentJson = Serialize(planResult.TestIntent),
                    RisksJson = Serialize(planResult.Risks),
                    Summary = planResult.Summary,
                }
            );
        }
        else
        {
            existing.Scope = (int)planResult.Scope;
            existing.RepositoriesJson = Serialize(planResult.Repositories);
            existing.ValidationTargetsJson = Serialize(planResult.ValidationTargets);
            existing.TestIntentJson = Serialize(planResult.TestIntent);
            existing.RisksJson = Serialize(planResult.Risks);
            existing.Summary = planResult.Summary;
        }
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<PlanResult?> GetPlanAsync(string executionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context
            .ExecutionPlans.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ExecutionId == executionId, cancellationToken);

        return entity is null
            ? null
            : new PlanResult(
                (WorkItemScope)entity.Scope,
                Deserialize(entity.RepositoriesJson),
                Deserialize(entity.ValidationTargetsJson),
                Deserialize(entity.TestIntentJson),
                Deserialize(entity.RisksJson),
                entity.Summary
            );
    }

    public async Task SaveReviewResultAsync(
        string executionId,
        ReviewResult reviewResult,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.ReviewResults.Add(
            new ReviewResultEntity
            {
                ExecutionId = executionId,
                Summary = reviewResult.Summary,
                TranscriptPath = reviewResult.TranscriptPath,
                StructuredPayload = reviewResult.StructuredPayload,
            }
        );
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<ReviewResult?> GetLatestReviewResultAsync(string executionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context
            .ReviewResults.AsNoTracking()
            .Where(r => r.ExecutionId == executionId)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return entity is null ? null : Agents.ReviewResultParser.Parse(entity.StructuredPayload, entity.TranscriptPath);
    }

    public async Task RecordAgentUsageAsync(
        string executionId,
        TaskPhase phase,
        AgentRole role,
        AgentProvider provider,
        AgentUsage usage,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.AgentUsages.Add(
            new AgentUsageEntity
            {
                ExecutionId = executionId,
                Phase = (int)phase,
                Role = (int)role,
                Provider = (int)provider,
                ActualModel = usage.ActualModel,
                InputTokens = usage.InputTokens,
                OutputTokens = usage.OutputTokens,
                CachedInputTokens = usage.CachedInputTokens,
                CostUsd = usage.CostUsd?.ToString("F6", CultureInfo.InvariantCulture),
                Timestamp = DateTimeOffset.UtcNow,
            }
        );
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentUsageRecord>> GetAgentUsageAsync(
        string executionId,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .AgentUsages.AsNoTracking()
            .Where(u => u.ExecutionId == executionId)
            .OrderBy(u => u.Id)
            .Select(u => new AgentUsageRecord(
                u.Id,
                u.ExecutionId,
                (TaskPhase)u.Phase,
                (AgentRole)u.Role,
                (AgentProvider)u.Provider,
                u.ActualModel,
                u.InputTokens,
                u.OutputTokens,
                u.CachedInputTokens,
                u.CostUsd == null ? (decimal?)null : decimal.Parse(u.CostUsd, CultureInfo.InvariantCulture),
                u.Timestamp
            ))
            .ToListAsync(cancellationToken);
    }

    public async Task LinkPullRequestToExecutionAsync(
        string executionId,
        int pullRequestId,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var exists = await context.ExecutionPrLinks.AnyAsync(
            l => l.ExecutionId == executionId && l.PullRequestId == pullRequestId,
            cancellationToken
        );
        if (!exists)
        {
            context.ExecutionPrLinks.Add(
                new ExecutionPrLinkEntity { ExecutionId = executionId, PullRequestId = pullRequestId }
            );
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<TaskExecutionRecord>> ListRelatedExecutionsByPrAsync(
        string executionId,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Find all executions that share at least one PR with the given execution.
        var prIds = await context
            .ExecutionPrLinks.Where(l => l.ExecutionId == executionId)
            .Select(l => l.PullRequestId)
            .ToListAsync(cancellationToken);

        if (prIds.Count == 0)
            return [];

        return await context
            .Executions.AsNoTracking()
            .Where(e =>
                e.ExecutionId != executionId
                && context.ExecutionPrLinks.Any(l => l.ExecutionId == e.ExecutionId && prIds.Contains(l.PullRequestId))
            )
            .OrderByDescending(e => e.UpdatedAt)
            .Select(e => ToDomain(e))
            .ToListAsync(cancellationToken);
    }

    private static ExecutionEntity ToEntity(TaskExecutionRecord record) =>
        new()
        {
            ExecutionId = record.ExecutionId,
            WorkItemId = record.WorkItemId,
            Title = record.Title,
            Kind = (int)record.Kind,
            Phase = (int)record.Phase,
            Status = (int)record.Status,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
            PlanProviderSessionId = record.ProviderPlanSessionId,
            PullRequestId = record.PullRequestId,
        };

    private static TaskExecutionRecord ToDomain(ExecutionEntity entity) =>
        new(
            entity.ExecutionId,
            entity.WorkItemId,
            entity.Title,
            (QueueItemKind)entity.Kind,
            (TaskPhase)entity.Phase,
            (ExecutionStatus)entity.Status,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.PlanProviderSessionId,
            entity.PullRequestId
        );

    private static string Serialize(IReadOnlyList<string> values) => JsonSerializer.Serialize(values);

    private static IReadOnlyList<string> Deserialize(string json) => JsonSerializer.Deserialize<string[]>(json) ?? [];

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
