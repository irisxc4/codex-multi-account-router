using System.Text.Json;
using CodexRouter.Domain;

namespace CodexRouter.Storage;

public sealed partial class RouterRepository
{
    private static readonly JsonSerializerOptions StorageJsonOptions = new(JsonSerializerDefaults.Web);

    public Task<long> AppendHealthEventAsync(AccountHealth health, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(health);
        return ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO health_events(account_id, state, reason, cooldown_until, created_at)
                VALUES ($account, $state, $reason, $cooldown, $created);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$account", health.AccountId.Value);
            command.Parameters.AddWithValue("$state", HealthStateToDb(health.State));
            command.Parameters.AddWithValue("$reason", (object?)health.Reason ?? DBNull.Value);
            command.Parameters.AddWithValue("$cooldown", health.CooldownUntil is null
                ? DBNull.Value
                : (object)ToDbTime(health.CooldownUntil.Value));
            command.Parameters.AddWithValue("$created", ToDbTime(health.CheckedAt));
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
        }, "Failed to append health event.", cancellationToken);
    }

    public Task<IReadOnlyList<HealthEventRecord>> GetHealthEventsAsync(
        AccountId accountId,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return ExecuteAsync<IReadOnlyList<HealthEventRecord>>(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, account_id, state, reason, cooldown_until, created_at
                FROM health_events
                WHERE account_id = $account
                ORDER BY created_at DESC, id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$account", accountId.Value);
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var items = new List<HealthEventRecord>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var createdAt = FromDbTime(reader.GetInt64(5));
                var cooldown = ReadNullableInt64(reader, 4);
                var health = new AccountHealth(
                    new AccountId(reader.GetString(1)),
                    HealthStateFromDb(reader.GetString(2)),
                    createdAt,
                    ReadNullableString(reader, 3),
                    cooldown is null ? null : FromDbTime(cooldown.Value));
                items.Add(new HealthEventRecord(reader.GetInt64(0), health, createdAt));
            }
            return items;
        }, "Failed to read health events.", cancellationToken);
    }

    public Task<RouterSettings> GetRouterSettingsAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT mode, pinned_account_id, short_reserve_percent, long_reserve_percent,
                       quota_stale_after_seconds, worker_idle_timeout_seconds, updated_at
                FROM router_settings WHERE id = 1;
                """;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new StorageException("router_settings singleton row is missing.");
            }

            return new RouterSettings(
                RouterModeFromDb(reader.GetString(0)),
                reader.IsDBNull(1) ? null : new AccountId(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetInt32(3),
                TimeSpan.FromSeconds(reader.GetInt64(4)),
                TimeSpan.FromSeconds(reader.GetInt64(5)),
                FromDbTime(reader.GetInt64(6)));
        }, "Failed to read router settings.", cancellationToken);

    public Task UpdateRouterSettingsAsync(RouterSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.ShortReservePercent is < 0 or > 100 ||
            settings.LongReservePercent is < 0 or > 100 ||
            settings.QuotaStaleAfter <= TimeSpan.Zero ||
            settings.WorkerIdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Router setting values are outside allowed bounds.");
        }
        if (settings.Mode == RouterMode.Pinned && settings.PinnedAccountId is null)
        {
            throw new ArgumentException("Pinned router mode requires a pinned account.", nameof(settings));
        }

        return ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE router_settings
                SET mode = $mode,
                    pinned_account_id = $pinned,
                    short_reserve_percent = $short,
                    long_reserve_percent = $long,
                    quota_stale_after_seconds = $stale,
                    worker_idle_timeout_seconds = $idle,
                    updated_at = $updated
                WHERE id = 1;
                """;
            command.Parameters.AddWithValue("$mode", RouterModeToDb(settings.Mode));
            command.Parameters.AddWithValue("$pinned", settings.PinnedAccountId is null
                ? DBNull.Value
                : (object)settings.PinnedAccountId.Value.Value);
            command.Parameters.AddWithValue("$short", settings.ShortReservePercent);
            command.Parameters.AddWithValue("$long", settings.LongReservePercent);
            command.Parameters.AddWithValue("$stale", checked((long)settings.QuotaStaleAfter.TotalSeconds));
            command.Parameters.AddWithValue("$idle", checked((long)settings.WorkerIdleTimeout.TotalSeconds));
            command.Parameters.AddWithValue("$updated", ToDbTime(settings.UpdatedAt));
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new StorageException("router_settings singleton row is missing.");
            }
        }, "Failed to update router settings atomically.", cancellationToken);
    }

    public Task<long> AppendCompatibilityRunAsync(CompatibilityReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        return ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO compatibility_runs(
                    state, binary_path, binary_version, binary_sha256, checked_at, report_json)
                VALUES ($state, $path, $version, $sha, $checked, $report);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$state", CompatibilityStateToDb(report.State));
            command.Parameters.AddWithValue("$path", (object?)report.Binary?.Path ?? DBNull.Value);
            command.Parameters.AddWithValue("$version", (object?)report.Binary?.Version ?? DBNull.Value);
            command.Parameters.AddWithValue("$sha", (object?)report.Binary?.Sha256 ?? DBNull.Value);
            command.Parameters.AddWithValue("$checked", ToDbTime(report.CheckedAt));
            command.Parameters.AddWithValue("$report", JsonSerializer.Serialize(report, StorageJsonOptions));
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
        }, "Failed to append compatibility run.", cancellationToken);
    }

    public Task<CompatibilityRunRecord?> GetLatestCompatibilityRunAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, report_json FROM compatibility_runs ORDER BY checked_at DESC, id DESC LIMIT 1;";
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            var report = JsonSerializer.Deserialize<CompatibilityReport>(reader.GetString(1), StorageJsonOptions)
                ?? throw new StorageException("Stored compatibility report could not be deserialized.");
            return new CompatibilityRunRecord(reader.GetInt64(0), report);
        }, "Failed to read latest compatibility run.", cancellationToken);

    public Task<long> StartWorkerSessionAsync(
        WorkerId workerId,
        AccountId accountId,
        WorkerState state,
        int? processId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO worker_sessions(worker_id, account_id, state, process_id, started_at)
                VALUES ($worker, $account, $state, $pid, $started);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$worker", workerId.Value);
            command.Parameters.AddWithValue("$account", accountId.Value);
            command.Parameters.AddWithValue("$state", WorkerStateToDb(state));
            command.Parameters.AddWithValue("$pid", processId is null ? DBNull.Value : processId.Value);
            command.Parameters.AddWithValue("$started", ToDbTime(startedAt));
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
        }, "Failed to start worker session record.", cancellationToken);

    public Task<bool> FinishWorkerSessionAsync(
        long sessionId,
        WorkerState state,
        DateTimeOffset endedAt,
        int? exitCode,
        string? failure,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE worker_sessions
                SET state = $state, ended_at = $ended, exit_code = $exit, failure = $failure
                WHERE id = $id AND ended_at IS NULL;
                """;
            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$state", WorkerStateToDb(state));
            command.Parameters.AddWithValue("$ended", ToDbTime(endedAt));
            command.Parameters.AddWithValue("$exit", exitCode is null ? DBNull.Value : exitCode.Value);
            command.Parameters.AddWithValue("$failure", (object?)failure ?? DBNull.Value);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, "Failed to finish worker session record.", cancellationToken);

    public Task CreateMigrationJobAsync(MigrationJobRecord job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (string.IsNullOrWhiteSpace(job.Id))
        {
            throw new ArgumentException("Migration job id cannot be empty.", nameof(job));
        }

        return ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO migration_jobs(
                    id, source_thread_id, source_account_id, destination_account_id,
                    destination_thread_id, state, created_at, updated_at, failure)
                VALUES ($id, $thread, $source, $destination, $destination_thread, $state, $created, $updated, $failure);
                """;
            command.Parameters.AddWithValue("$id", job.Id);
            command.Parameters.AddWithValue("$thread", job.SourceThreadId.Value);
            command.Parameters.AddWithValue("$source", job.SourceAccountId.Value);
            command.Parameters.AddWithValue("$destination", job.DestinationAccountId.Value);
            command.Parameters.AddWithValue("$destination_thread", job.DestinationThreadId is null
                ? DBNull.Value
                : (object)job.DestinationThreadId.Value.Value);
            command.Parameters.AddWithValue("$state", MigrationStatusToDb(job.Status));
            command.Parameters.AddWithValue("$created", ToDbTime(job.CreatedAt));
            command.Parameters.AddWithValue("$updated", ToDbTime(job.UpdatedAt));
            command.Parameters.AddWithValue("$failure", (object?)job.Failure ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, "Failed to create migration job.", cancellationToken);
    }

    public Task<MigrationJobRecord?> GetMigrationJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, source_thread_id, source_account_id, destination_account_id,
                       destination_thread_id, state, created_at, updated_at, failure
                FROM migration_jobs WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", jobId);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            return new MigrationJobRecord(
                reader.GetString(0),
                new ThreadId(reader.GetString(1)),
                new AccountId(reader.GetString(2)),
                new AccountId(reader.GetString(3)),
                reader.IsDBNull(4) ? null : new ThreadId(reader.GetString(4)),
                MigrationStatusFromDb(reader.GetString(5)),
                FromDbTime(reader.GetInt64(6)),
                FromDbTime(reader.GetInt64(7)),
                ReadNullableString(reader, 8));
        }, "Failed to read migration job.", cancellationToken);

    public Task<bool> TransitionMigrationJobAsync(
        string jobId,
        MigrationJobStatus expectedStatus,
        MigrationJobStatus nextStatus,
        DateTimeOffset updatedAt,
        ThreadId? destinationThreadId = null,
        string? failure = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE migration_jobs
                SET state = $next,
                    destination_thread_id = COALESCE($destination_thread, destination_thread_id),
                    updated_at = $updated,
                    failure = $failure
                WHERE id = $id AND state = $expected;
                """;
            command.Parameters.AddWithValue("$id", jobId);
            command.Parameters.AddWithValue("$expected", MigrationStatusToDb(expectedStatus));
            command.Parameters.AddWithValue("$next", MigrationStatusToDb(nextStatus));
            command.Parameters.AddWithValue("$destination_thread", destinationThreadId is null
                ? DBNull.Value
                : (object)destinationThreadId.Value.Value);
            command.Parameters.AddWithValue("$updated", ToDbTime(updatedAt));
            command.Parameters.AddWithValue("$failure", (object?)failure ?? DBNull.Value);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, "Failed to transition migration job.", cancellationToken);

    private static string RouterModeToDb(RouterMode mode) => mode switch
    {
        RouterMode.Auto => "auto",
        RouterMode.Pinned => "pinned",
        RouterMode.Off => "off",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    private static RouterMode RouterModeFromDb(string value) => value switch
    {
        "auto" => RouterMode.Auto,
        "pinned" => RouterMode.Pinned,
        "off" => RouterMode.Off,
        _ => throw new StorageException($"Unknown router mode '{value}'.")
    };

    private static string HealthStateToDb(AccountHealthState state) => state.ToString().ToLowerInvariant();

    private static AccountHealthState HealthStateFromDb(string value) =>
        Enum.TryParse<AccountHealthState>(value, ignoreCase: true, out var state)
            ? state
            : throw new StorageException($"Unknown account health state '{value}'.");

    private static string WorkerStateToDb(WorkerState state) => state.ToString().ToLowerInvariant();

    private static string CompatibilityStateToDb(CompatibilityState state) => state.ToString().ToLowerInvariant();

    private static string MigrationStatusToDb(MigrationJobStatus status) => status switch
    {
        MigrationJobStatus.Pending => "pending",
        MigrationJobStatus.Snapshotting => "snapshotting",
        MigrationJobStatus.CreatingDestination => "creating_destination",
        MigrationJobStatus.Linking => "linking",
        MigrationJobStatus.Completed => "completed",
        MigrationJobStatus.Failed => "failed",
        MigrationJobStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    private static MigrationJobStatus MigrationStatusFromDb(string value) => value switch
    {
        "pending" => MigrationJobStatus.Pending,
        "snapshotting" => MigrationJobStatus.Snapshotting,
        "creating_destination" => MigrationJobStatus.CreatingDestination,
        "linking" => MigrationJobStatus.Linking,
        "completed" => MigrationJobStatus.Completed,
        "failed" => MigrationJobStatus.Failed,
        "cancelled" => MigrationJobStatus.Cancelled,
        _ => throw new StorageException($"Unknown migration job status '{value}'.")
    };
}
