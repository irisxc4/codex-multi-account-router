using CodexRouter.Domain;

namespace CodexRouter.Storage;

public sealed record RouteDecisionAuditRecord(
    string Id,
    ThreadId? ThreadId,
    AccountId? WinnerAccountId,
    string Reason,
    string DecisionJson,
    DateTimeOffset CreatedAt);

public sealed partial class RouterRepository
{
    public Task CommitThreadRouteWithAuditAsync(
        ThreadRoute route,
        string decisionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (string.IsNullOrWhiteSpace(decisionId))
        {
            throw new ArgumentException("Decision id cannot be empty.", nameof(decisionId));
        }

        return ExecuteAsync(async (connection, ct) =>
        {
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                    insert.CommandText = """
                        INSERT INTO thread_routes(thread_id, account_id, worker_id, reason, created_at, last_used_at)
                        VALUES ($thread, $account, $worker, $reason, $created, $used);
                        """;
                    insert.Parameters.AddWithValue("$thread", route.ThreadId.Value);
                    insert.Parameters.AddWithValue("$account", route.AccountId.Value);
                    insert.Parameters.AddWithValue("$worker", route.WorkerId.Value);
                    insert.Parameters.AddWithValue("$reason", RouteReasonToDb(route.Reason));
                    insert.Parameters.AddWithValue("$created", ToDbTime(route.CreatedAt));
                    insert.Parameters.AddWithValue("$used", ToDbTime(route.LastUsedAt));
                    await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (var attach = connection.CreateCommand())
                {
                    attach.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                    attach.CommandText = "UPDATE route_decisions SET thread_id = $thread WHERE id = $id AND thread_id IS NULL;";
                    attach.Parameters.AddWithValue("$thread", route.ThreadId.Value);
                    attach.Parameters.AddWithValue("$id", decisionId);
                    if (await attach.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                    {
                        throw new StorageException($"Route decision '{decisionId}' is missing or already attached.");
                    }
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }, "Failed to commit sticky thread route with its decision audit.", cancellationToken);
    }

    public Task AppendRouteDecisionAuditAsync(
        RouteDecisionAuditRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.Id) || string.IsNullOrWhiteSpace(record.Reason) || string.IsNullOrWhiteSpace(record.DecisionJson))
        {
            throw new ArgumentException("Route decision audit record is incomplete.", nameof(record));
        }

        return ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO route_decisions(id, thread_id, winner_account_id, reason, decision_json, created_at)
                VALUES ($id, $thread, $winner, $reason, $json, $created);
                """;
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$thread", record.ThreadId is null ? DBNull.Value : (object)record.ThreadId.Value.Value);
            command.Parameters.AddWithValue("$winner", record.WinnerAccountId is null ? DBNull.Value : (object)record.WinnerAccountId.Value.Value);
            command.Parameters.AddWithValue("$reason", record.Reason);
            command.Parameters.AddWithValue("$json", record.DecisionJson);
            command.Parameters.AddWithValue("$created", ToDbTime(record.CreatedAt));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, "Failed to append route decision audit.", cancellationToken);
    }

    public Task<IReadOnlyList<RouteDecisionAuditRecord>> GetRouteDecisionAuditsAsync(
        ThreadId? threadId = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return ExecuteAsync<IReadOnlyList<RouteDecisionAuditRecord>>(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = threadId is null
                ? "SELECT id, thread_id, winner_account_id, reason, decision_json, created_at FROM route_decisions ORDER BY created_at DESC LIMIT $limit;"
                : "SELECT id, thread_id, winner_account_id, reason, decision_json, created_at FROM route_decisions WHERE thread_id = $thread ORDER BY created_at DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", limit);
            if (threadId is not null)
            {
                command.Parameters.AddWithValue("$thread", threadId.Value.Value);
            }
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var records = new List<RouteDecisionAuditRecord>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                records.Add(new RouteDecisionAuditRecord(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : new ThreadId(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : new AccountId(reader.GetString(2)),
                    reader.GetString(3),
                    reader.GetString(4),
                    FromDbTime(reader.GetInt64(5))));
            }
            return records;
        }, "Failed to read route decision audits.", cancellationToken);
    }
}
