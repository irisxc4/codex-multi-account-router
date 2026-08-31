using CodexRouter.Domain;
using Microsoft.Data.Sqlite;

namespace CodexRouter.Storage;

public sealed partial class RouterRepository
{
    public Task<long> AppendQuotaSnapshotAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return ExecuteAsync(async (connection, ct) =>
        {
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                long snapshotId;
                await using (var header = connection.CreateCommand())
                {
                    header.Transaction = (SqliteTransaction)transaction;
                    header.CommandText = """
                        INSERT INTO quota_snapshots(
                            account_id, fetched_at, plan_type, reached_type, spend_control_reached,
                            has_credits, unlimited_credits, credit_balance)
                        VALUES ($account, $fetched, $plan, $reached, $spend, $has, $unlimited, $balance);
                        SELECT last_insert_rowid();
                        """;
                    header.Parameters.AddWithValue("$account", snapshot.AccountId.Value);
                    header.Parameters.AddWithValue("$fetched", ToDbTime(snapshot.FetchedAt));
                    header.Parameters.AddWithValue("$plan", (object?)snapshot.PlanType ?? DBNull.Value);
                    header.Parameters.AddWithValue("$reached", (object?)snapshot.RateLimitReachedType ?? DBNull.Value);
                    header.Parameters.AddWithValue("$spend", DbBool(snapshot.SpendControlReached));
                    header.Parameters.AddWithValue("$has", DbBool(snapshot.HasCredits));
                    header.Parameters.AddWithValue("$unlimited", DbBool(snapshot.UnlimitedCredits));
                    header.Parameters.AddWithValue("$balance", (object?)snapshot.CreditBalance ?? DBNull.Value);
                    snapshotId = Convert.ToInt64(await header.ExecuteScalarAsync(ct).ConfigureAwait(false),
                        System.Globalization.CultureInfo.InvariantCulture);
                }

                for (var index = 0; index < snapshot.Buckets.Count; index++)
                {
                    var bucket = snapshot.Buckets[index];
                    await using var command = connection.CreateCommand();
                    command.Transaction = (SqliteTransaction)transaction;
                    command.CommandText = """
                        INSERT INTO quota_buckets(
                            snapshot_id, ordinal, limit_id, limit_name, slot, used_percent,
                            window_duration_seconds, resets_at)
                        VALUES ($snapshot, $ordinal, $limit, $name, $slot, $used, $duration, $reset);
                        """;
                    command.Parameters.AddWithValue("$snapshot", snapshotId);
                    command.Parameters.AddWithValue("$ordinal", index);
                    command.Parameters.AddWithValue("$limit", bucket.LimitId);
                    command.Parameters.AddWithValue("$name", (object?)bucket.LimitName ?? DBNull.Value);
                    command.Parameters.AddWithValue("$slot", QuotaSlotToDb(bucket.Slot));
                    command.Parameters.AddWithValue("$used", bucket.UsedPercent);
                    command.Parameters.AddWithValue("$duration",
                        bucket.WindowDuration is null ? DBNull.Value : checked((object)(long)bucket.WindowDuration.Value.TotalSeconds));
                    command.Parameters.AddWithValue("$reset",
                        bucket.ResetsAt is null ? DBNull.Value : (object)bucket.ResetsAt.Value.ToUnixTimeMilliseconds());
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return snapshotId;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }, "Failed to append quota snapshot.", cancellationToken);
    }

    public Task<QuotaSnapshot?> GetLatestQuotaSnapshotAsync(AccountId accountId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (connection, ct) =>
        {
            var ids = await ReadSnapshotIdsAsync(connection,
                "SELECT id FROM quota_snapshots WHERE account_id = $account ORDER BY fetched_at DESC, id DESC LIMIT 1;",
                accountId, 1, ct).ConfigureAwait(false);
            return ids.Count == 0 ? null : await ReadQuotaSnapshotAsync(connection, ids[0], ct).ConfigureAwait(false);
        }, "Failed to read latest quota snapshot.", cancellationToken);

    public Task<IReadOnlyList<QuotaSnapshot>> GetQuotaHistoryAsync(
        AccountId accountId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return ExecuteAsync<IReadOnlyList<QuotaSnapshot>>(async (connection, ct) =>
        {
            var ids = await ReadSnapshotIdsAsync(connection,
                "SELECT id FROM quota_snapshots WHERE account_id = $account ORDER BY fetched_at DESC, id DESC LIMIT $limit;",
                accountId, limit, ct).ConfigureAwait(false);
            var items = new List<QuotaSnapshot>(ids.Count);
            foreach (var id in ids)
            {
                items.Add(await ReadQuotaSnapshotAsync(connection, id, ct).ConfigureAwait(false));
            }
            return items;
        }, "Failed to read quota history.", cancellationToken);
    }

    public Task<long> AppendUsageSnapshotAsync(UsageSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return ExecuteAsync(async (connection, ct) =>
        {
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                long snapshotId;
                await using (var header = connection.CreateCommand())
                {
                    header.Transaction = (SqliteTransaction)transaction;
                    header.CommandText = """
                        INSERT INTO usage_snapshots(
                            account_id, fetched_at, lifetime_tokens, peak_daily_tokens,
                            longest_running_turn_seconds, current_streak_days, longest_streak_days)
                        VALUES ($account, $fetched, $lifetime, $peak, $longturn, $current, $longest);
                        SELECT last_insert_rowid();
                        """;
                    header.Parameters.AddWithValue("$account", snapshot.AccountId.Value);
                    header.Parameters.AddWithValue("$fetched", ToDbTime(snapshot.FetchedAt));
                    header.Parameters.AddWithValue("$lifetime", DbLong(snapshot.LifetimeTokens));
                    header.Parameters.AddWithValue("$peak", DbLong(snapshot.PeakDailyTokens));
                    header.Parameters.AddWithValue("$longturn", DbLong(snapshot.LongestRunningTurnSeconds));
                    header.Parameters.AddWithValue("$current", DbLong(snapshot.CurrentStreakDays));
                    header.Parameters.AddWithValue("$longest", DbLong(snapshot.LongestStreakDays));
                    snapshotId = Convert.ToInt64(await header.ExecuteScalarAsync(ct).ConfigureAwait(false),
                        System.Globalization.CultureInfo.InvariantCulture);
                }

                for (var index = 0; index < snapshot.DailyBuckets.Count; index++)
                {
                    var bucket = snapshot.DailyBuckets[index];
                    await using var command = connection.CreateCommand();
                    command.Transaction = (SqliteTransaction)transaction;
                    command.CommandText = """
                        INSERT INTO usage_daily_buckets(snapshot_id, ordinal, start_date, tokens)
                        VALUES ($snapshot, $ordinal, $date, $tokens);
                        """;
                    command.Parameters.AddWithValue("$snapshot", snapshotId);
                    command.Parameters.AddWithValue("$ordinal", index);
                    command.Parameters.AddWithValue("$date", bucket.StartDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                    command.Parameters.AddWithValue("$tokens", bucket.Tokens);
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return snapshotId;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }, "Failed to append usage snapshot.", cancellationToken);
    }

    public Task<UsageSnapshot?> GetLatestUsageSnapshotAsync(AccountId accountId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (connection, ct) =>
        {
            var ids = await ReadSnapshotIdsAsync(connection,
                "SELECT id FROM usage_snapshots WHERE account_id = $account ORDER BY fetched_at DESC, id DESC LIMIT 1;",
                accountId, 1, ct).ConfigureAwait(false);
            return ids.Count == 0 ? null : await ReadUsageSnapshotAsync(connection, ids[0], ct).ConfigureAwait(false);
        }, "Failed to read latest usage snapshot.", cancellationToken);

    public Task<IReadOnlyList<UsageSnapshot>> GetUsageHistoryAsync(
        AccountId accountId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return ExecuteAsync<IReadOnlyList<UsageSnapshot>>(async (connection, ct) =>
        {
            var ids = await ReadSnapshotIdsAsync(connection,
                "SELECT id FROM usage_snapshots WHERE account_id = $account ORDER BY fetched_at DESC, id DESC LIMIT $limit;",
                accountId, limit, ct).ConfigureAwait(false);
            var items = new List<UsageSnapshot>(ids.Count);
            foreach (var id in ids)
            {
                items.Add(await ReadUsageSnapshotAsync(connection, id, ct).ConfigureAwait(false));
            }
            return items;
        }, "Failed to read usage history.", cancellationToken);
    }

    private static async Task<List<long>> ReadSnapshotIdsAsync(
        SqliteConnection connection,
        string sql,
        AccountId accountId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$account", accountId.Value);
        if (sql.Contains("$limit", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("$limit", limit);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ids = new List<long>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    private static async Task<QuotaSnapshot> ReadQuotaSnapshotAsync(
        SqliteConnection connection,
        long snapshotId,
        CancellationToken cancellationToken)
    {
        AccountId accountId;
        DateTimeOffset fetchedAt;
        string? planType;
        string? reached;
        bool? spend;
        bool? has;
        bool? unlimited;
        string? balance;

        await using (var header = connection.CreateCommand())
        {
            header.CommandText = """
                SELECT account_id, fetched_at, plan_type, reached_type, spend_control_reached,
                       has_credits, unlimited_credits, credit_balance
                FROM quota_snapshots WHERE id = $id;
                """;
            header.Parameters.AddWithValue("$id", snapshotId);
            await using var reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new StorageException($"Quota snapshot {snapshotId} disappeared while reading.");
            }
            accountId = new AccountId(reader.GetString(0));
            fetchedAt = FromDbTime(reader.GetInt64(1));
            planType = ReadNullableString(reader, 2);
            reached = ReadNullableString(reader, 3);
            spend = ReadNullableBoolean(reader, 4);
            has = ReadNullableBoolean(reader, 5);
            unlimited = ReadNullableBoolean(reader, 6);
            balance = ReadNullableString(reader, 7);
        }

        var buckets = new List<QuotaBucket>();
        await using (var detail = connection.CreateCommand())
        {
            detail.CommandText = """
                SELECT limit_id, limit_name, slot, used_percent, window_duration_seconds, resets_at
                FROM quota_buckets WHERE snapshot_id = $id ORDER BY ordinal ASC;
                """;
            detail.Parameters.AddWithValue("$id", snapshotId);
            await using var reader = await detail.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var durationSeconds = ReadNullableInt64(reader, 4);
                var resetMs = ReadNullableInt64(reader, 5);
                buckets.Add(new QuotaBucket(
                    reader.GetString(0),
                    ReadNullableString(reader, 1),
                    QuotaSlotFromDb(reader.GetString(2)),
                    reader.GetInt32(3),
                    durationSeconds is null ? null : TimeSpan.FromSeconds(durationSeconds.Value),
                    resetMs is null ? null : FromDbTime(resetMs.Value)));
            }
        }

        return new QuotaSnapshot(accountId, buckets, fetchedAt, planType, reached, spend, has, unlimited, balance);
    }

    private static async Task<UsageSnapshot> ReadUsageSnapshotAsync(
        SqliteConnection connection,
        long snapshotId,
        CancellationToken cancellationToken)
    {
        AccountId accountId;
        DateTimeOffset fetchedAt;
        long? lifetime;
        long? peak;
        long? longTurn;
        long? current;
        long? longest;

        await using (var header = connection.CreateCommand())
        {
            header.CommandText = """
                SELECT account_id, fetched_at, lifetime_tokens, peak_daily_tokens,
                       longest_running_turn_seconds, current_streak_days, longest_streak_days
                FROM usage_snapshots WHERE id = $id;
                """;
            header.Parameters.AddWithValue("$id", snapshotId);
            await using var reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new StorageException($"Usage snapshot {snapshotId} disappeared while reading.");
            }
            accountId = new AccountId(reader.GetString(0));
            fetchedAt = FromDbTime(reader.GetInt64(1));
            lifetime = ReadNullableInt64(reader, 2);
            peak = ReadNullableInt64(reader, 3);
            longTurn = ReadNullableInt64(reader, 4);
            current = ReadNullableInt64(reader, 5);
            longest = ReadNullableInt64(reader, 6);
        }

        var daily = new List<UsageDailyBucket>();
        await using (var detail = connection.CreateCommand())
        {
            detail.CommandText = """
                SELECT start_date, tokens FROM usage_daily_buckets
                WHERE snapshot_id = $id ORDER BY ordinal ASC;
                """;
            detail.Parameters.AddWithValue("$id", snapshotId);
            await using var reader = await detail.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                daily.Add(new UsageDailyBucket(
                    DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    reader.GetInt64(1)));
            }
        }

        return new UsageSnapshot(accountId, fetchedAt, lifetime, peak, longTurn, current, longest, daily);
    }

    private static object DbBool(bool? value) => value is null ? DBNull.Value : value.Value ? 1 : 0;
    private static object DbLong(long? value) => value is null ? DBNull.Value : value.Value;

    private static string QuotaSlotToDb(QuotaBucketSlot slot) => slot switch
    {
        QuotaBucketSlot.Primary => "primary",
        QuotaBucketSlot.Secondary => "secondary",
        _ => "other"
    };

    private static QuotaBucketSlot QuotaSlotFromDb(string value) => value switch
    {
        "primary" => QuotaBucketSlot.Primary,
        "secondary" => QuotaBucketSlot.Secondary,
        _ => QuotaBucketSlot.Other
    };
}
