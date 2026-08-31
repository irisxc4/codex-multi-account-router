using CodexRouter.Domain;

namespace CodexRouter.Storage;

public sealed record OrphanThreadRecord(
    ThreadId ThreadId,
    AccountId AccountId,
    WorkerId WorkerId,
    string Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

public sealed partial class RouterRepository
{
    public Task RecordOrphanThreadAsync(
        OrphanThreadRecord orphan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orphan);
        return ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO orphan_threads(thread_id, account_id, worker_id, reason, created_at, resolved_at)
                VALUES ($thread, $account, $worker, $reason, $created, $resolved)
                ON CONFLICT(thread_id, account_id) DO UPDATE SET
                    worker_id = excluded.worker_id,
                    reason = excluded.reason,
                    created_at = excluded.created_at,
                    resolved_at = excluded.resolved_at;
                """;
            command.Parameters.AddWithValue("$thread", orphan.ThreadId.Value);
            command.Parameters.AddWithValue("$account", orphan.AccountId.Value);
            command.Parameters.AddWithValue("$worker", orphan.WorkerId.Value);
            command.Parameters.AddWithValue("$reason", orphan.Reason);
            command.Parameters.AddWithValue("$created", ToDbTime(orphan.CreatedAt));
            command.Parameters.AddWithValue("$resolved", orphan.ResolvedAt is null ? DBNull.Value : (object)ToDbTime(orphan.ResolvedAt.Value));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, "Failed to record orphan thread.", cancellationToken);
    }

    public Task<bool> ResolveOrphanThreadAsync(
        ThreadId threadId,
        AccountId accountId,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE orphan_threads SET resolved_at = $resolved
                WHERE thread_id = $thread AND account_id = $account AND resolved_at IS NULL;
                """;
            command.Parameters.AddWithValue("$thread", threadId.Value);
            command.Parameters.AddWithValue("$account", accountId.Value);
            command.Parameters.AddWithValue("$resolved", ToDbTime(resolvedAt));
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, "Failed to resolve orphan thread.", cancellationToken);

    public Task<IReadOnlyList<OrphanThreadRecord>> ListUnresolvedOrphanThreadsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        return ExecuteAsync<IReadOnlyList<OrphanThreadRecord>>(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT thread_id, account_id, worker_id, reason, created_at, resolved_at
                FROM orphan_threads
                WHERE resolved_at IS NULL
                ORDER BY created_at DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var records = new List<OrphanThreadRecord>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                records.Add(new OrphanThreadRecord(
                    new ThreadId(reader.GetString(0)),
                    new AccountId(reader.GetString(1)),
                    new WorkerId(reader.GetString(2)),
                    reader.GetString(3),
                    FromDbTime(reader.GetInt64(4)),
                    reader.IsDBNull(5) ? null : FromDbTime(reader.GetInt64(5))));
            }
            return records;
        }, "Failed to list unresolved orphan threads.", cancellationToken);
    }
}
