using CodexRouter.Domain;

namespace CodexRouter.Storage;

public sealed record StoredThreadMigrationJob(
    string Id,
    ThreadId SourceThreadId,
    AccountId SourceAccountId,
    AccountId TargetAccountId,
    ThreadId? TargetThreadId,
    string State,
    string? SnapshotJson,
    string? HandoffText,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record ThreadMigrationEventRecord(
    long Id,
    string JobId,
    string? FromState,
    string ToState,
    string? Message,
    DateTimeOffset CreatedAt);

public sealed partial class RouterRepository
{
    public Task CreateThreadMigrationJobAsync(
        StoredThreadMigrationJob job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.SourceAccountId == job.TargetAccountId)
        {
            throw new ArgumentException("Migration source and target accounts must differ.", nameof(job));
        }

        return ExecuteAsync(async (connection, ct) =>
        {
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await using (var activeCheck = connection.CreateCommand())
                {
                    activeCheck.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                    activeCheck.CommandText = """
                        SELECT id
                        FROM thread_migration_jobs
                        WHERE source_thread_id = $sourceThread
                          AND state IN ('pending', 'snapshotting', 'creating-target', 'seeding')
                        LIMIT 1;
                        """;
                    activeCheck.Parameters.AddWithValue("$sourceThread", job.SourceThreadId.Value);
                    var activeJobId = await activeCheck.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
                    if (activeJobId is not null)
                    {
                        throw new StorageException(
                            $"Source thread '{job.SourceThreadId}' already has active migration '{activeJobId}'.");
                    }
                }

                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                    insert.CommandText = """
                        INSERT INTO thread_migration_jobs(
                            id, source_thread_id, source_account_id, target_account_id, target_thread_id,
                            state, snapshot_json, handoff_text, error, created_at, updated_at, completed_at)
                        VALUES (
                            $id, $sourceThread, $sourceAccount, $targetAccount, $targetThread,
                            $state, $snapshot, $handoff, $error, $created, $updated, $completed);
                        """;
                    BindMigrationJob(insert, job);
                    await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (var eventInsert = connection.CreateCommand())
                {
                    eventInsert.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                    eventInsert.CommandText = """
                        INSERT INTO thread_migration_events(job_id, from_state, to_state, message, created_at)
                        VALUES ($job, NULL, $to, $message, $created);
                        """;
                    eventInsert.Parameters.AddWithValue("$job", job.Id);
                    eventInsert.Parameters.AddWithValue("$to", job.State);
                    eventInsert.Parameters.AddWithValue("$message", DBNull.Value);
                    eventInsert.Parameters.AddWithValue("$created", ToDbTime(job.CreatedAt));
                    await eventInsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }, "Failed to create thread migration job.", cancellationToken);
    }

    public Task<StoredThreadMigrationJob?> GetThreadMigrationJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Migration job id is required.", nameof(jobId));
        return ExecuteAsync<StoredThreadMigrationJob?>(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, source_thread_id, source_account_id, target_account_id, target_thread_id,
                       state, snapshot_json, handoff_text, error, created_at, updated_at, completed_at
                FROM thread_migration_jobs WHERE id = $id LIMIT 1;
                """;
            command.Parameters.AddWithValue("$id", jobId);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadMigrationJob(reader) : null;
        }, "Failed to read thread migration job.", cancellationToken);
    }

    public Task<IReadOnlyList<StoredThreadMigrationJob>> ListThreadMigrationJobsAsync(
        ThreadId? sourceThreadId = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(limit));
        return ExecuteAsync<IReadOnlyList<StoredThreadMigrationJob>>(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sourceThreadId is null
                ? """
                    SELECT id, source_thread_id, source_account_id, target_account_id, target_thread_id,
                           state, snapshot_json, handoff_text, error, created_at, updated_at, completed_at
                    FROM thread_migration_jobs ORDER BY updated_at DESC, id DESC LIMIT $limit;
                    """
                : """
                    SELECT id, source_thread_id, source_account_id, target_account_id, target_thread_id,
                           state, snapshot_json, handoff_text, error, created_at, updated_at, completed_at
                    FROM thread_migration_jobs WHERE source_thread_id = $thread
                    ORDER BY updated_at DESC, id DESC LIMIT $limit;
                    """;
            command.Parameters.AddWithValue("$limit", limit);
            if (sourceThreadId is not null) command.Parameters.AddWithValue("$thread", sourceThreadId.Value.Value);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var jobs = new List<StoredThreadMigrationJob>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) jobs.Add(ReadMigrationJob(reader));
            return jobs;
        }, "Failed to list thread migration jobs.", cancellationToken);
    }

    public Task TransitionThreadMigrationJobAsync(
        string jobId,
        string expectedState,
        string nextState,
        ThreadId? targetThreadId = null,
        string? snapshotJson = null,
        string? handoffText = null,
        string? error = null,
        string? message = null,
        DateTimeOffset? changedAt = null,
        bool markCompleted = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Migration job id is required.", nameof(jobId));
        if (string.IsNullOrWhiteSpace(expectedState)) throw new ArgumentException("Expected state is required.", nameof(expectedState));
        if (string.IsNullOrWhiteSpace(nextState)) throw new ArgumentException("Next state is required.", nameof(nextState));
        var now = changedAt ?? DateTimeOffset.UtcNow;

        return ExecuteAsync(async (connection, ct) =>
        {
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await using (var update = connection.CreateCommand())
                {
                    update.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                    update.CommandText = """
                        UPDATE thread_migration_jobs SET
                            state = $next,
                            target_thread_id = COALESCE($targetThread, target_thread_id),
                            snapshot_json = COALESCE($snapshot, snapshot_json),
                            handoff_text = COALESCE($handoff, handoff_text),
                            error = $error,
                            updated_at = $updated,
                            completed_at = CASE WHEN $markCompleted = 1 THEN $updated ELSE completed_at END
                        WHERE id = $id AND state = $expected;
                        """;
                    update.Parameters.AddWithValue("$next", nextState);
                    update.Parameters.AddWithValue("$targetThread", targetThreadId is null ? DBNull.Value : (object)targetThreadId.Value.Value);
                    update.Parameters.AddWithValue("$snapshot", snapshotJson is null ? DBNull.Value : (object)snapshotJson);
                    update.Parameters.AddWithValue("$handoff", handoffText is null ? DBNull.Value : (object)handoffText);
                    update.Parameters.AddWithValue("$error", error is null ? DBNull.Value : (object)error);
                    update.Parameters.AddWithValue("$updated", ToDbTime(now));
                    update.Parameters.AddWithValue("$markCompleted", markCompleted ? 1 : 0);
                    update.Parameters.AddWithValue("$id", jobId);
                    update.Parameters.AddWithValue("$expected", expectedState);
                    if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                    {
                        throw new StorageException($"Migration job '{jobId}' is not in expected state '{expectedState}'.");
                    }
                }

                await using (var eventInsert = connection.CreateCommand())
                {
                    eventInsert.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                    eventInsert.CommandText = """
                        INSERT INTO thread_migration_events(job_id, from_state, to_state, message, created_at)
                        VALUES ($job, $from, $to, $message, $created);
                        """;
                    eventInsert.Parameters.AddWithValue("$job", jobId);
                    eventInsert.Parameters.AddWithValue("$from", expectedState);
                    eventInsert.Parameters.AddWithValue("$to", nextState);
                    eventInsert.Parameters.AddWithValue("$message", message is null ? DBNull.Value : (object)message);
                    eventInsert.Parameters.AddWithValue("$created", ToDbTime(now));
                    await eventInsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }, "Failed to transition thread migration job.", cancellationToken);
    }

    public Task<IReadOnlyList<ThreadMigrationEventRecord>> GetThreadMigrationEventsAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Migration job id is required.", nameof(jobId));
        return ExecuteAsync<IReadOnlyList<ThreadMigrationEventRecord>>(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, job_id, from_state, to_state, message, created_at
                FROM thread_migration_events WHERE job_id = $job ORDER BY created_at ASC, id ASC;
                """;
            command.Parameters.AddWithValue("$job", jobId);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var events = new List<ThreadMigrationEventRecord>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                events.Add(new ThreadMigrationEventRecord(
                    reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), FromDbTime(reader.GetInt64(5))));
            }
            return events;
        }, "Failed to read thread migration events.", cancellationToken);
    }

    private static void BindMigrationJob(Microsoft.Data.Sqlite.SqliteCommand command, StoredThreadMigrationJob job)
    {
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$sourceThread", job.SourceThreadId.Value);
        command.Parameters.AddWithValue("$sourceAccount", job.SourceAccountId.Value);
        command.Parameters.AddWithValue("$targetAccount", job.TargetAccountId.Value);
        command.Parameters.AddWithValue("$targetThread", job.TargetThreadId is null ? DBNull.Value : (object)job.TargetThreadId.Value.Value);
        command.Parameters.AddWithValue("$state", job.State);
        command.Parameters.AddWithValue("$snapshot", job.SnapshotJson is null ? DBNull.Value : (object)job.SnapshotJson);
        command.Parameters.AddWithValue("$handoff", job.HandoffText is null ? DBNull.Value : (object)job.HandoffText);
        command.Parameters.AddWithValue("$error", job.Error is null ? DBNull.Value : (object)job.Error);
        command.Parameters.AddWithValue("$created", ToDbTime(job.CreatedAt));
        command.Parameters.AddWithValue("$updated", ToDbTime(job.UpdatedAt));
        command.Parameters.AddWithValue("$completed", job.CompletedAt is null ? DBNull.Value : (object)ToDbTime(job.CompletedAt.Value));
    }

    private static StoredThreadMigrationJob ReadMigrationJob(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            new ThreadId(reader.GetString(1)),
            new AccountId(reader.GetString(2)),
            new AccountId(reader.GetString(3)),
            reader.IsDBNull(4) ? null : new ThreadId(reader.GetString(4)),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            FromDbTime(reader.GetInt64(9)),
            FromDbTime(reader.GetInt64(10)),
            reader.IsDBNull(11) ? null : FromDbTime(reader.GetInt64(11)));
}
