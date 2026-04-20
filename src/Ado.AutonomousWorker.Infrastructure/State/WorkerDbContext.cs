using Ado.AutonomousWorker.Infrastructure.State.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ado.AutonomousWorker.Infrastructure.State;

internal sealed class WorkerDbContext(DbContextOptions<WorkerDbContext> options) : DbContext(options)
{
    public DbSet<ExecutionEntity> Executions => Set<ExecutionEntity>();
    public DbSet<ExecutionEventEntity> Events => Set<ExecutionEventEntity>();
    public DbSet<AgentSessionEntity> Sessions => Set<AgentSessionEntity>();
    public DbSet<ReviewResultEntity> ReviewResults => Set<ReviewResultEntity>();
    public DbSet<ExecutionPlanEntity> ExecutionPlans => Set<ExecutionPlanEntity>();
    public DbSet<AgentOutputChunkEntity> AgentOutputChunks => Set<AgentOutputChunkEntity>();
    public DbSet<AgentUsageEntity> AgentUsages => Set<AgentUsageEntity>();
    public DbSet<ExecutionPrLinkEntity> ExecutionPrLinks => Set<ExecutionPrLinkEntity>();

    // SQLite does not support DateTimeOffset natively in ORDER BY; store as ISO 8601 text instead.
    // ISO 8601 strings sort lexicographically in the same order as DateTimeOffset chronologically.
    private static readonly ValueConverter<DateTimeOffset, string> DateTimeOffsetConverter = new(
        v => v.ToString("O"),
        v => DateTimeOffset.Parse(v)
    );

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExecutionEntity>(entity =>
        {
            entity.ToTable("executions");
            entity.HasKey(e => e.ExecutionId);
            entity.Property(e => e.ExecutionId).HasColumnName("execution_id");
            entity.Property(e => e.WorkItemId).HasColumnName("work_item_id");
            entity.Property(e => e.Title).HasColumnName("title");
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.Phase).HasColumnName("phase");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasConversion(DateTimeOffsetConverter);
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasConversion(DateTimeOffsetConverter);
            entity.Property(e => e.PlanProviderSessionId).HasColumnName("plan_provider_session_id");
        });

        modelBuilder.Entity<ExecutionEventEntity>(entity =>
        {
            entity.ToTable("events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ExecutionId).HasColumnName("execution_id");
            entity.Property(e => e.Timestamp).HasColumnName("timestamp").HasConversion(DateTimeOffsetConverter);
            entity.Property(e => e.Phase).HasColumnName("phase");
            entity.Property(e => e.Message).HasColumnName("message");
        });

        modelBuilder.Entity<AgentSessionEntity>(entity =>
        {
            entity.ToTable("sessions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ExecutionId).HasColumnName("execution_id");
            entity.Property(e => e.SessionId).HasColumnName("session_id");
            entity.Property(e => e.Provider).HasColumnName("provider");
            entity.Property(e => e.Role).HasColumnName("role");
            entity.Property(e => e.WorkDir).HasColumnName("workdir");
            entity.Property(e => e.TranscriptPath).HasColumnName("transcript_path");
            entity.Property(e => e.ProviderSessionId).HasColumnName("provider_session_id");
        });

        modelBuilder.Entity<ReviewResultEntity>(entity =>
        {
            entity.ToTable("review_results");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ExecutionId).HasColumnName("execution_id");
            entity.Property(e => e.Summary).HasColumnName("summary");
            entity.Property(e => e.TranscriptPath).HasColumnName("transcript_path");
            entity.Property(e => e.StructuredPayload).HasColumnName("structured_payload");
        });

        modelBuilder.Entity<ExecutionPlanEntity>(entity =>
        {
            entity.ToTable("execution_plans");
            entity.HasKey(e => e.ExecutionId);
            entity.Property(e => e.ExecutionId).HasColumnName("execution_id");
            entity.Property(e => e.Scope).HasColumnName("scope");
            entity.Property(e => e.RepositoriesJson).HasColumnName("repositories_json");
            entity.Property(e => e.ValidationTargetsJson).HasColumnName("validation_targets_json");
            entity.Property(e => e.TestIntentJson).HasColumnName("test_intent_json");
            entity.Property(e => e.RisksJson).HasColumnName("risks_json");
            entity.Property(e => e.Summary).HasColumnName("summary");
        });

        modelBuilder.Entity<AgentOutputChunkEntity>(entity =>
        {
            entity.ToTable("agent_output_chunks");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ExecutionId).HasColumnName("execution_id");
            entity.Property(e => e.SessionId).HasColumnName("session_id");
            entity.Property(e => e.Provider).HasColumnName("provider");
            entity.Property(e => e.Role).HasColumnName("role");
            entity.Property(e => e.WorkDir).HasColumnName("workdir");
            entity.Property(e => e.Stream).HasColumnName("stream");
            entity.Property(e => e.Sequence).HasColumnName("sequence");
            entity.Property(e => e.Timestamp).HasColumnName("timestamp").HasConversion(DateTimeOffsetConverter);
            entity.Property(e => e.Content).HasColumnName("content");
        });

        modelBuilder.Entity<ExecutionPrLinkEntity>(entity =>
        {
            entity.ToTable("execution_pr_links");
            entity.HasKey(e => new { e.ExecutionId, e.PullRequestId });
            entity.Property(e => e.ExecutionId).HasColumnName("execution_id");
            entity.Property(e => e.PullRequestId).HasColumnName("pull_request_id");
            entity.Property(e => e.RepositoryName).HasColumnName("repository_name");
        });

        modelBuilder.Entity<AgentUsageEntity>(entity =>
        {
            entity.ToTable("agent_usage");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ExecutionId).HasColumnName("execution_id");
            entity.Property(e => e.Phase).HasColumnName("phase");
            entity.Property(e => e.Role).HasColumnName("role");
            entity.Property(e => e.Provider).HasColumnName("provider");
            entity.Property(e => e.ActualModel).HasColumnName("actual_model");
            entity.Property(e => e.InputTokens).HasColumnName("input_tokens");
            entity.Property(e => e.OutputTokens).HasColumnName("output_tokens");
            entity.Property(e => e.CachedInputTokens).HasColumnName("cached_input_tokens");
            entity.Property(e => e.CostUsd).HasColumnName("cost_usd");
            entity.Property(e => e.Timestamp).HasColumnName("timestamp").HasConversion(DateTimeOffsetConverter);
        });
    }
}
