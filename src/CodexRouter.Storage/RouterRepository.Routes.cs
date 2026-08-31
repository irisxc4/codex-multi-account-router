using CodexRouter.Domain;

namespace CodexRouter.Storage;

public sealed partial class RouterRepository
{
    public Task InsertThreadRouteAsync(ThreadRoute route, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        return ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO thread_routes(thread_id, account_id, worker_id, reason, created_at, last_used_at)
                VALUES ($thread, $account, $worker, $reason, $created, $used);
                """;
            command.Parameters.AddWithValue("$thread", route.ThreadId.Value);
            command.Parameters.AddWithValue("$account", route.AccountId.Value);
            command.Parameters.AddWithValue("$worker", route.WorkerId.Value);
            command.Parameters.AddWithValue("$reason", RouteReasonToDb(route.Reason));
            command.Parameters.AddWithValue("$created", ToDbTime(route.CreatedAt));
            command.Parameters.AddWithValue("$used", ToDbTime(route.LastUsedAt));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, "Failed to insert sticky thread route.", cancellationToken);
    }

    public Task<ThreadRoute?> GetThreadRouteAsync(ThreadId threadId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT thread_id, account_id, worker_id, reason, created_at, last_used_at
                FROM thread_routes WHERE thread_id = $thread;
                """;
            command.Parameters.AddWithValue("$thread", threadId.Value);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            return new ThreadRoute(
                new ThreadId(reader.GetString(0)),
                new AccountId(reader.GetString(1)),
                new WorkerId(reader.GetString(2)),
                RouteReasonFromDb(reader.GetString(3)),
                FromDbTime(reader.GetInt64(4)),
                FromDbTime(reader.GetInt64(5)));
        }, "Failed to read sticky thread route.", cancellationToken);

    public Task<bool> DeleteThreadRouteAsync(ThreadId threadId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM thread_routes WHERE thread_id = $thread;";
            command.Parameters.AddWithValue("$thread", threadId.Value);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, "Failed to delete sticky thread route.", cancellationToken);

    public Task<bool> TouchThreadRouteAsync(
        ThreadId threadId,
        DateTimeOffset lastUsedAt,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE thread_routes SET last_used_at = $used WHERE thread_id = $thread;";
            command.Parameters.AddWithValue("$thread", threadId.Value);
            command.Parameters.AddWithValue("$used", ToDbTime(lastUsedAt));
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, "Failed to touch sticky thread route.", cancellationToken);

    public Task<bool> ReassignThreadRouteAsync(
        ThreadRoute replacement,
        AccountId expectedCurrentAccount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        return ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE thread_routes
                SET account_id = $account,
                    worker_id = $worker,
                    reason = $reason,
                    last_used_at = $used
                WHERE thread_id = $thread AND account_id = $expected;
                """;
            command.Parameters.AddWithValue("$thread", replacement.ThreadId.Value);
            command.Parameters.AddWithValue("$account", replacement.AccountId.Value);
            command.Parameters.AddWithValue("$worker", replacement.WorkerId.Value);
            command.Parameters.AddWithValue("$reason", RouteReasonToDb(replacement.Reason));
            command.Parameters.AddWithValue("$used", ToDbTime(replacement.LastUsedAt));
            command.Parameters.AddWithValue("$expected", expectedCurrentAccount.Value);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, "Failed to reassign sticky thread route.", cancellationToken);
    }

    public Task<IReadOnlyList<ThreadRoute>> ListThreadRoutesForAccountAsync(
        AccountId accountId,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return ExecuteAsync<IReadOnlyList<ThreadRoute>>(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT thread_id, account_id, worker_id, reason, created_at, last_used_at
                FROM thread_routes
                WHERE account_id = $account
                ORDER BY last_used_at DESC, thread_id ASC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$account", accountId.Value);
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var routes = new List<ThreadRoute>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                routes.Add(new ThreadRoute(
                    new ThreadId(reader.GetString(0)),
                    new AccountId(reader.GetString(1)),
                    new WorkerId(reader.GetString(2)),
                    RouteReasonFromDb(reader.GetString(3)),
                    FromDbTime(reader.GetInt64(4)),
                    FromDbTime(reader.GetInt64(5))));
            }
            return routes;
        }, "Failed to list sticky routes for account.", cancellationToken);
    }

    private static string RouteReasonToDb(RouteReason reason) => reason switch
    {
        RouteReason.AutoQuota => "auto_quota",
        RouteReason.ManualPin => "manual_pin",
        RouteReason.Sticky => "sticky",
        RouteReason.Recovery => "recovery",
        RouteReason.Migration => "migration",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };

    private static RouteReason RouteReasonFromDb(string value) => value switch
    {
        "auto_quota" => RouteReason.AutoQuota,
        "manual_pin" => RouteReason.ManualPin,
        "sticky" => RouteReason.Sticky,
        "recovery" => RouteReason.Recovery,
        "migration" => RouteReason.Migration,
        _ => throw new StorageException($"Unknown route reason '{value}' in storage.")
    };
}
