using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Infrastructure.State;
using Microsoft.EntityFrameworkCore;

namespace Ado.AutonomousWorker.Tests;

public sealed class SqliteStateStoreTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "ado-worker-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public async Task AppendAgentOutputChunkAsync_TrimsStoredChunksPerSession()
    {
        Directory.CreateDirectory(_rootPath);
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);

        for (var index = 1; index <= 205; index++)
        {
            await store.AppendAgentOutputChunkAsync(
                "exec-1",
                new AgentOutputChunk(
                    "session-1",
                    AgentProvider.Claude,
                    AgentRole.Code,
                    "D:\\worktree",
                    AgentOutputStream.Stdout,
                    index,
                    DateTimeOffset.UtcNow.AddSeconds(index),
                    $"line-{index}"
                ),
                CancellationToken.None
            );
        }

        var chunks = await store.GetAgentOutputChunksAsync("exec-1", CancellationToken.None);

        Assert.Equal(200, chunks.Count);
        Assert.Equal(6, chunks.First().Sequence);
        Assert.Equal(205, chunks.Last().Sequence);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootPath))
                Directory.Delete(_rootPath, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private SqliteStateStore CreateStore()
    {
        var dbPath = Path.Combine(_rootPath, "worker.db");
        var factory = new TestDbContextFactory(dbPath);
        return new SqliteStateStore(factory);
    }

    private sealed class TestDbContextFactory(string dbPath) : IDbContextFactory<WorkerDbContext>
    {
        public WorkerDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<WorkerDbContext>().UseSqlite($"Data Source={dbPath}").Options;
            return new WorkerDbContext(options);
        }
    }
}
