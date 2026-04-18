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

    [Test]
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

        await Assert.That(chunks.Count).IsEqualTo(200);
        await Assert.That(chunks.First().Sequence).IsEqualTo(6);
        await Assert.That(chunks.Last().Sequence).IsEqualTo(205);
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
