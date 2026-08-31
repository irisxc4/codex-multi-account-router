using CodexRouter.Domain;
using Microsoft.Data.Sqlite;

namespace CodexRouter.Storage;

public sealed partial class RouterRepository
{
    public Task CreateAccountAsync(
        AccountProfile profile,
        DateTimeOffset? createdAt = null,
        AccountLifecycle lifecycle = AccountLifecycle.Active,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ExecuteAsync(async (connection, ct) =>
        {
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                var now = createdAt ?? DateTimeOffset.UtcNow;
                await using (var account = connection.CreateCommand())
                {
                    account.Transaction = (SqliteTransaction)transaction;
                    account.CommandText = """
                        INSERT INTO accounts(id, alias, email, plan_type, codex_home, enabled, priority, created_at, last_seen_at, lifecycle)
                        VALUES ($id, $alias, $email, $plan, $home, $enabled, $priority, $created, NULL, $lifecycle);
                        """;
                    account.Parameters.AddWithValue("$id", profile.Id.Value);
                    account.Parameters.AddWithValue("$alias", profile.Alias);
                    account.Parameters.AddWithValue("$email", (object?)profile.Email ?? DBNull.Value);
                    account.Parameters.AddWithValue("$plan", (object?)profile.PlanType ?? DBNull.Value);
                    account.Parameters.AddWithValue("$home", profile.CodexHome);
                    account.Parameters.AddWithValue("$enabled", profile.Enabled ? 1 : 0);
                    account.Parameters.AddWithValue("$priority", profile.Priority);
                    account.Parameters.AddWithValue("$created", ToDbTime(now));
                    account.Parameters.AddWithValue("$lifecycle", LifecycleToDb(lifecycle));
                    await account.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (var preferences = connection.CreateCommand())
                {
                    preferences.Transaction = (SqliteTransaction)transaction;
                    preferences.CommandText = """
                        INSERT INTO account_preferences(account_id, route_weight, short_reserve_percent, long_reserve_percent, updated_at)
                        VALUES ($id, 1.0, 15, 8, $updated);
                        """;
                    preferences.Parameters.AddWithValue("$id", profile.Id.Value);
                    preferences.Parameters.AddWithValue("$updated", ToDbTime(now));
                    await preferences.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }, "Failed to create account.", cancellationToken);
    }

    public Task<StoredAccount?> GetAccountAsync(AccountId accountId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, alias, email, plan_type, codex_home, enabled, priority, created_at, last_seen_at, lifecycle
                FROM accounts WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", accountId.Value);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadStoredAccount(reader) : null;
        }, "Failed to read account.", cancellationToken);

    public Task<IReadOnlyList<StoredAccount>> ListAccountsAsync(CancellationToken cancellationToken = default) =>
        ListAccountsCoreAsync(includePending: false, cancellationToken);

    public Task<IReadOnlyList<StoredAccount>> ListAllAccountsAsync(CancellationToken cancellationToken = default) =>
        ListAccountsCoreAsync(includePending: true, cancellationToken);

    private Task<IReadOnlyList<StoredAccount>> ListAccountsCoreAsync(
        bool includePending,
        CancellationToken cancellationToken) =>
        ExecuteAsync<IReadOnlyList<StoredAccount>>(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = includePending
                ? """
                    SELECT id, alias, email, plan_type, codex_home, enabled, priority, created_at, last_seen_at, lifecycle
                    FROM accounts ORDER BY priority DESC, created_at ASC, id ASC;
                    """
                : """
                    SELECT id, alias, email, plan_type, codex_home, enabled, priority, created_at, last_seen_at, lifecycle
                    FROM accounts
                    WHERE lifecycle = 'active'
                    ORDER BY priority DESC, created_at ASC, id ASC;
                    """;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var items = new List<StoredAccount>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(ReadStoredAccount(reader));
            }
            return items;
        }, "Failed to list accounts.", cancellationToken);

    public Task<bool> UpdateAccountAsync(AccountProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE accounts
                SET alias = $alias,
                    email = $email,
                    plan_type = $plan,
                    codex_home = $home,
                    enabled = $enabled,
                    priority = $priority
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", profile.Id.Value);
            command.Parameters.AddWithValue("$alias", profile.Alias);
            command.Parameters.AddWithValue("$email", (object?)profile.Email ?? DBNull.Value);
            command.Parameters.AddWithValue("$plan", (object?)profile.PlanType ?? DBNull.Value);
            command.Parameters.AddWithValue("$home", profile.CodexHome);
            command.Parameters.AddWithValue("$enabled", profile.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$priority", profile.Priority);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, "Failed to update account.", cancellationToken);
    }

    public Task<bool> SetAccountLifecycleAsync(
        AccountId accountId,
        AccountLifecycle lifecycle,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE accounts SET lifecycle = $lifecycle WHERE id = $id;";
            command.Parameters.AddWithValue("$id", accountId.Value);
            command.Parameters.AddWithValue("$lifecycle", LifecycleToDb(lifecycle));
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, "Failed to update account lifecycle.", cancellationToken);

    public Task<bool> SetAccountLastSeenAsync(
        AccountId accountId,
        DateTimeOffset lastSeenAt,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE accounts SET last_seen_at = $seen WHERE id = $id;";
            command.Parameters.AddWithValue("$id", accountId.Value);
            command.Parameters.AddWithValue("$seen", ToDbTime(lastSeenAt));
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, "Failed to update account last-seen timestamp.", cancellationToken);

    public Task<bool> DeleteAccountAsync(AccountId accountId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM accounts WHERE id = $id;";
            command.Parameters.AddWithValue("$id", accountId.Value);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, "Failed to delete account.", cancellationToken);

    public Task<AccountPreferences?> GetAccountPreferencesAsync(
        AccountId accountId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT account_id, route_weight, short_reserve_percent, long_reserve_percent, updated_at
                FROM account_preferences WHERE account_id = $id;
                """;
            command.Parameters.AddWithValue("$id", accountId.Value);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            return new AccountPreferences(
                new AccountId(reader.GetString(0)),
                reader.GetDouble(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                FromDbTime(reader.GetInt64(4)));
        }, "Failed to read account preferences.", cancellationToken);

    public Task<bool> UpdateAccountPreferencesAsync(
        AccountPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (preferences.RouteWeight < 0 ||
            preferences.ShortReservePercent is < 0 or > 100 ||
            preferences.LongReservePercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(preferences), "Account preference values are outside allowed bounds.");
        }

        return ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE account_preferences
                SET route_weight = $weight,
                    short_reserve_percent = $short,
                    long_reserve_percent = $long,
                    updated_at = $updated
                WHERE account_id = $id;
                """;
            command.Parameters.AddWithValue("$id", preferences.AccountId.Value);
            command.Parameters.AddWithValue("$weight", preferences.RouteWeight);
            command.Parameters.AddWithValue("$short", preferences.ShortReservePercent);
            command.Parameters.AddWithValue("$long", preferences.LongReservePercent);
            command.Parameters.AddWithValue("$updated", ToDbTime(preferences.UpdatedAt));
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, "Failed to update account preferences.", cancellationToken);
    }

    private static StoredAccount ReadStoredAccount(SqliteDataReader reader)
    {
        var profile = new AccountProfile(
            new AccountId(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(4),
            ReadNullableString(reader, 2),
            ReadNullableString(reader, 3),
            reader.GetInt64(5) != 0,
            reader.GetInt32(6));

        return new StoredAccount(
            profile,
            FromDbTime(reader.GetInt64(7)),
            reader.IsDBNull(8) ? null : FromDbTime(reader.GetInt64(8)),
            LifecycleFromDb(reader.GetString(9)));
    }

    private static string LifecycleToDb(AccountLifecycle lifecycle) => lifecycle switch
    {
        AccountLifecycle.Pending => "pending",
        AccountLifecycle.Active => "active",
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle), lifecycle, "Unknown account lifecycle.")
    };

    private static AccountLifecycle LifecycleFromDb(string value) => value switch
    {
        "pending" => AccountLifecycle.Pending,
        "active" => AccountLifecycle.Active,
        _ => throw new StorageException($"Unknown account lifecycle '{value}'.")
    };
}
