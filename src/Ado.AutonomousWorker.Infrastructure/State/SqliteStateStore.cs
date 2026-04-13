using System.Text.Json;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.State;

public sealed class SqliteStateStore(IOptions<StorageOptions> storageOptions) : IStateStore
{
    private readonly string _databasePath = storageOptions.Value.DatabasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);

        var sql = """
        CREATE TABLE IF NOT EXISTS executions (
            execution_id TEXT PRIMARY KEY,
            work_item_id INTEGER NOT NULL,
            title TEXT NOT NULL,
            kind INTEGER NOT NULL,
            phase INTEGER NOT NULL,
            status INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            plan_provider_session_id TEXT NULL
        );
        CREATE TABLE IF NOT EXISTS events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            execution_id TEXT NOT NULL,
            timestamp TEXT NOT NULL,
            phase INTEGER NOT NULL,
            message TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS sessions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            execution_id TEXT NOT NULL,
            session_id TEXT NOT NULL,
            provider INTEGER NOT NULL,
            role INTEGER NOT NULL,
            workdir TEXT NOT NULL,
            transcript_path TEXT NOT NULL,
            provider_session_id TEXT NULL
        );
        CREATE TABLE IF NOT EXISTS review_results (
            execution_id TEXT NOT NULL,
            summary TEXT NOT NULL,
            transcript_path TEXT NOT NULL,
            structured_payload TEXT NULL
        );
        CREATE TABLE IF NOT EXISTS execution_plans (
            execution_id TEXT PRIMARY KEY,
            scope INTEGER NOT NULL,
            repositories_json TEXT NOT NULL,
            validation_targets_json TEXT NOT NULL,
            test_intent_json TEXT NOT NULL,
            risks_json TEXT NOT NULL,
            summary TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS agent_output_chunks (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            execution_id TEXT NOT NULL,
            session_id TEXT NOT NULL,
            provider INTEGER NOT NULL,
            role INTEGER NOT NULL,
            workdir TEXT NOT NULL,
            stream INTEGER NOT NULL,
            sequence INTEGER NOT NULL,
            timestamp TEXT NOT NULL,
            content TEXT NOT NULL
        );
        """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);

        // Idempotent migration: add status column if it does not exist yet.
        await using var migrationCheck = connection.CreateCommand();
        migrationCheck.CommandText = "SELECT COUNT(*) FROM pragma_table_info('executions') WHERE name='status';";
        var hasStatus = (long)(await migrationCheck.ExecuteScalarAsync(cancellationToken))! > 0;
        if (!hasStatus)
        {
            await using var alterTable = connection.CreateCommand();
            alterTable.CommandText = "ALTER TABLE executions ADD COLUMN status INTEGER NOT NULL DEFAULT 0;";
            await alterTable.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task SaveExecutionAsync(TaskExecutionRecord execution, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
        INSERT OR REPLACE INTO executions
        (execution_id, work_item_id, title, kind, phase, status, created_at, updated_at, plan_provider_session_id)
        VALUES ($id, $workItemId, $title, $kind, $phase, $status, $createdAt, $updatedAt, $planSessionId);
        """;
        command.Parameters.AddWithValue("$id", execution.ExecutionId);
        command.Parameters.AddWithValue("$workItemId", execution.WorkItemId);
        command.Parameters.AddWithValue("$title", execution.Title);
        command.Parameters.AddWithValue("$kind", (int)execution.Kind);
        command.Parameters.AddWithValue("$phase", (int)execution.Phase);
        command.Parameters.AddWithValue("$status", (int)execution.Status);
        command.Parameters.AddWithValue("$createdAt", execution.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", execution.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$planSessionId", (object?)execution.ProviderPlanSessionId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TaskExecutionRecord?> GetExecutionAsync(string executionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT execution_id, work_item_id, title, kind, phase, status, created_at, updated_at, plan_provider_session_id
        FROM executions
        WHERE execution_id = $id;
        """;
        command.Parameters.AddWithValue("$id", executionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadExecution(reader);
    }

    public async Task<IReadOnlyList<TaskExecutionRecord>> ListExecutionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT execution_id, work_item_id, title, kind, phase, status, created_at, updated_at, plan_provider_session_id
        FROM executions
        ORDER BY updated_at DESC;
        """;

        var results = new List<TaskExecutionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadExecution(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<TaskExecutionRecord>> ListActiveExecutionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT execution_id, work_item_id, title, kind, phase, status, created_at, updated_at, plan_provider_session_id
        FROM executions
        WHERE status = $running
        ORDER BY updated_at DESC;
        """;
        command.Parameters.AddWithValue("$running", (int)ExecutionStatus.Running);

        var results = new List<TaskExecutionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadExecution(reader));
        }

        return results;
    }

    public async Task UpdatePhaseAsync(string executionId, TaskPhase phase, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
        UPDATE executions
        SET phase = $phase,
            updated_at = $updatedAt
        WHERE execution_id = $id;
        """;
        command.Parameters.AddWithValue("$id", executionId);
        command.Parameters.AddWithValue("$phase", (int)phase);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateStatusAsync(string executionId, ExecutionStatus status, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
        UPDATE executions
        SET status = $status,
            updated_at = $updatedAt
        WHERE execution_id = $id;
        """;
        command.Parameters.AddWithValue("$id", executionId);
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendEventAsync(ExecutionEventRecord executionEvent, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
        INSERT INTO events (execution_id, timestamp, phase, message)
        VALUES ($executionId, $timestamp, $phase, $message);
        """;
        command.Parameters.AddWithValue("$executionId", executionEvent.ExecutionId);
        command.Parameters.AddWithValue("$timestamp", executionEvent.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$phase", (int)executionEvent.Phase);
        command.Parameters.AddWithValue("$message", executionEvent.Message);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveAgentSessionAsync(string executionId, AgentSessionHandle session, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
        INSERT INTO sessions (execution_id, session_id, provider, role, workdir, transcript_path, provider_session_id)
        VALUES ($executionId, $sessionId, $provider, $role, $workdir, $transcript, $providerSessionId);
        """;
        command.Parameters.AddWithValue("$executionId", executionId);
        command.Parameters.AddWithValue("$sessionId", session.SessionId);
        command.Parameters.AddWithValue("$provider", (int)session.Provider);
        command.Parameters.AddWithValue("$role", (int)session.Role);
        command.Parameters.AddWithValue("$workdir", session.WorkingDirectory);
        command.Parameters.AddWithValue("$transcript", session.TranscriptPath);
        command.Parameters.AddWithValue("$providerSessionId", (object?)session.ProviderSessionId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentSessionHandle>> GetAgentSessionsAsync(string executionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT session_id, provider, role, workdir, transcript_path, provider_session_id
        FROM sessions
        WHERE execution_id = $executionId
        ORDER BY id;
        """;
        command.Parameters.AddWithValue("$executionId", executionId);

        var results = new List<AgentSessionHandle>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AgentSessionHandle(
                reader.GetString(0),
                (AgentProvider)reader.GetInt32(1),
                (AgentRole)reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return results;
    }

    public async Task<IReadOnlyList<ExecutionEventRecord>> GetEventsAsync(string executionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT execution_id, timestamp, phase, message
        FROM events
        WHERE execution_id = $executionId
        ORDER BY id;
        """;
        command.Parameters.AddWithValue("$executionId", executionId);

        var results = new List<ExecutionEventRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ExecutionEventRecord(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1)),
                (TaskPhase)reader.GetInt32(2),
                reader.GetString(3)));
        }

        return results;
    }

    public async Task AppendAgentOutputChunkAsync(string executionId, AgentOutputChunk chunk, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
            INSERT INTO agent_output_chunks
            (execution_id, session_id, provider, role, workdir, stream, sequence, timestamp, content)
            VALUES ($executionId, $sessionId, $provider, $role, $workdir, $stream, $sequence, $timestamp, $content);
            """;
            command.Parameters.AddWithValue("$executionId", executionId);
            command.Parameters.AddWithValue("$sessionId", chunk.SessionId);
            command.Parameters.AddWithValue("$provider", (int)chunk.Provider);
            command.Parameters.AddWithValue("$role", (int)chunk.Role);
            command.Parameters.AddWithValue("$workdir", chunk.WorkingDirectory);
            command.Parameters.AddWithValue("$stream", (int)chunk.Stream);
            command.Parameters.AddWithValue("$sequence", chunk.Sequence);
            command.Parameters.AddWithValue("$timestamp", chunk.Timestamp.ToString("O"));
            command.Parameters.AddWithValue("$content", Truncate(chunk.Content, 500));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var trimCommand = connection.CreateCommand();
        trimCommand.CommandText = """
        DELETE FROM agent_output_chunks
        WHERE id IN (
            SELECT id
            FROM agent_output_chunks
            WHERE execution_id = $executionId
              AND session_id = $sessionId
            ORDER BY sequence DESC
            LIMIT -1 OFFSET 200
        );
        """;
        trimCommand.Parameters.AddWithValue("$executionId", executionId);
        trimCommand.Parameters.AddWithValue("$sessionId", chunk.SessionId);
        await trimCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentOutputChunk>> GetAgentOutputChunksAsync(string executionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT session_id, provider, role, workdir, stream, sequence, timestamp, content
        FROM agent_output_chunks
        WHERE execution_id = $executionId
        ORDER BY timestamp, id;
        """;
        command.Parameters.AddWithValue("$executionId", executionId);

        var results = new List<AgentOutputChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AgentOutputChunk(
                reader.GetString(0),
                (AgentProvider)reader.GetInt32(1),
                (AgentRole)reader.GetInt32(2),
                reader.GetString(3),
                (AgentOutputStream)reader.GetInt32(4),
                reader.GetInt64(5),
                DateTimeOffset.Parse(reader.GetString(6)),
                reader.GetString(7)));
        }

        return results;
    }

    public async Task SavePlanAsync(string executionId, PlanResult planResult, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
        INSERT OR REPLACE INTO execution_plans
        (execution_id, scope, repositories_json, validation_targets_json, test_intent_json, risks_json, summary)
        VALUES ($executionId, $scope, $repositories, $validationTargets, $testIntent, $risks, $summary);
        """;
        command.Parameters.AddWithValue("$executionId", executionId);
        command.Parameters.AddWithValue("$scope", (int)planResult.Scope);
        command.Parameters.AddWithValue("$repositories", SerializeList(planResult.Repositories));
        command.Parameters.AddWithValue("$validationTargets", SerializeList(planResult.ValidationTargets));
        command.Parameters.AddWithValue("$testIntent", SerializeList(planResult.TestIntent));
        command.Parameters.AddWithValue("$risks", SerializeList(planResult.Risks));
        command.Parameters.AddWithValue("$summary", planResult.Summary);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PlanResult?> GetPlanAsync(string executionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT scope, repositories_json, validation_targets_json, test_intent_json, risks_json, summary
        FROM execution_plans
        WHERE execution_id = $executionId;
        """;
        command.Parameters.AddWithValue("$executionId", executionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PlanResult(
            (WorkItemScope)reader.GetInt32(0),
            DeserializeList(reader.GetString(1)),
            DeserializeList(reader.GetString(2)),
            DeserializeList(reader.GetString(3)),
            DeserializeList(reader.GetString(4)),
            reader.GetString(5));
    }

    public async Task SaveReviewResultAsync(string executionId, ReviewResult reviewResult, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
        INSERT INTO review_results (execution_id, summary, transcript_path, structured_payload)
        VALUES ($executionId, $summary, $transcriptPath, $payload);
        """;
        command.Parameters.AddWithValue("$executionId", executionId);
        command.Parameters.AddWithValue("$summary", reviewResult.Summary);
        command.Parameters.AddWithValue("$transcriptPath", reviewResult.TranscriptPath);
        command.Parameters.AddWithValue("$payload", (object?)reviewResult.StructuredPayload ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ReviewResult?> GetLatestReviewResultAsync(string executionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT summary, transcript_path, structured_payload
        FROM review_results
        WHERE execution_id = $executionId
        ORDER BY rowid DESC
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$executionId", executionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var payload = reader.IsDBNull(2) ? null : reader.GetString(2);
        return Agents.ReviewResultParser.Parse(payload, reader.GetString(1));
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static TaskExecutionRecord ReadExecution(SqliteDataReader reader)
    {
        return new TaskExecutionRecord(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            (QueueItemKind)reader.GetInt32(3),
            (TaskPhase)reader.GetInt32(4),
            (ExecutionStatus)reader.GetInt32(5),
            DateTimeOffset.Parse(reader.GetString(6)),
            DateTimeOffset.Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    private static string SerializeList(IReadOnlyList<string> values)
    {
        return JsonSerializer.Serialize(values);
    }

    private static IReadOnlyList<string> DeserializeList(string json)
    {
        return JsonSerializer.Deserialize<string[]>(json) ?? [];
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }
}
