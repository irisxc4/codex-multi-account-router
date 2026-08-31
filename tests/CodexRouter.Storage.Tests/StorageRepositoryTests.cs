using CodexRouter.Domain;
using CodexRouter.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexRouter.Storage.Tests;

public sealed class StorageRepositoryTests
{
    [Fact]
    public async Task Empty_database_initializes_and_migration_is_idempotent()
    {
        await using var fixture = await TempStorage.CreateAsync();

        await fixture.Database.InitializeAsync();
        await fixture.Database.InitializeAsync();

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
        Assert.Equal(6L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Account_crud_and_preferences_are_persistent()
    {
        await using var fixture = await TempStorage.CreateAsync();
        var account = Account("a", Path.Combine(fixture.Root, "profiles", "a"));

        await fixture.Repository.CreateAccountAsync(account);
        var stored = await fixture.Repository.GetAccountAsync(account.Id);
        Assert.NotNull(stored);
        Assert.Equal("A", stored!.Profile.Alias);

        var updated = account with { Alias = "Primary", Email = "a@example.com", Priority = 9 };
        Assert.True(await fixture.Repository.UpdateAccountAsync(updated));
        await fixture.Repository.SetAccountLastSeenAsync(account.Id, DateTimeOffset.UtcNow);

        var preferences = await fixture.Repository.GetAccountPreferencesAsync(account.Id);
        Assert.NotNull(preferences);
        Assert.True(await fixture.Repository.UpdateAccountPreferencesAsync(preferences! with
        {
            RouteWeight = 1.75,
            ShortReservePercent = 20,
            LongReservePercent = 10,
            UpdatedAt = DateTimeOffset.UtcNow
        }));

        stored = await fixture.Repository.GetAccountAsync(account.Id);
        Assert.Equal("Primary", stored!.Profile.Alias);
        Assert.Equal(9, stored.Profile.Priority);
        Assert.NotNull(stored.LastSeenAt);

        Assert.True(await fixture.Repository.DeleteAccountAsync(account.Id));
        Assert.Null(await fixture.Repository.GetAccountAsync(account.Id));
    }

    [Fact]
    public async Task Pending_accounts_are_persisted_but_hidden_from_normal_account_lists_until_activated()
    {
        await using var fixture = await TempStorage.CreateAsync();
        var account = Account("pending", Path.Combine(fixture.Root, "profiles", "pending")) with { Enabled = false };

        await fixture.Repository.CreateAccountAsync(account, lifecycle: AccountLifecycle.Pending);

        var stored = await fixture.Repository.GetAccountAsync(account.Id);
        Assert.NotNull(stored);
        Assert.Equal(AccountLifecycle.Pending, stored!.Lifecycle);
        Assert.Empty(await fixture.Repository.ListAccountsAsync());
        Assert.Single(await fixture.Repository.ListAllAccountsAsync());

        Assert.True(await fixture.Repository.SetAccountLifecycleAsync(account.Id, AccountLifecycle.Active));
        Assert.Single(await fixture.Repository.ListAccountsAsync());
        Assert.Equal(AccountLifecycle.Active, (await fixture.Repository.GetAccountAsync(account.Id))!.Lifecycle);
    }

    [Fact]
    public async Task Codex_home_is_case_insensitively_unique()
    {
        await using var fixture = await TempStorage.CreateAsync();
        var home = Path.Combine(fixture.Root, "profiles", "same-home");
        await fixture.Repository.CreateAccountAsync(Account("a", home));

        var duplicate = Account("b", home.ToUpperInvariant());
        await Assert.ThrowsAsync<StorageException>(() => fixture.Repository.CreateAccountAsync(duplicate));
    }

    [Fact]
    public async Task Quota_and_usage_history_round_trip_without_raw_protocol_shape()
    {
        await using var fixture = await TempStorage.CreateAsync();
        var account = Account("a", Path.Combine(fixture.Root, "profiles", "a"));
        await fixture.Repository.CreateAccountAsync(account);

        var now = DateTimeOffset.UtcNow;
        var quota = new QuotaSnapshot(account.Id, new[]
        {
            new QuotaBucket("codex", "Codex", QuotaBucketSlot.Primary, 58, TimeSpan.FromHours(5), now.AddHours(2)),
            new QuotaBucket("codex", "Codex", QuotaBucketSlot.Secondary, 29, TimeSpan.FromDays(7), now.AddDays(3))
        }, now, "plus", null, false, true, false, "4.20");
        await fixture.Repository.AppendQuotaSnapshotAsync(quota);

        var usage = new UsageSnapshot(account.Id, now, 1000, 500, 44, 3, 8, new[]
        {
            new UsageDailyBucket(new DateOnly(2026, 8, 15), 400),
            new UsageDailyBucket(new DateOnly(2026, 8, 16), 600)
        });
        await fixture.Repository.AppendUsageSnapshotAsync(usage);

        var storedQuota = await fixture.Repository.GetLatestQuotaSnapshotAsync(account.Id);
        var storedUsage = await fixture.Repository.GetLatestUsageSnapshotAsync(account.Id);

        Assert.NotNull(storedQuota);
        Assert.Equal(2, storedQuota!.Buckets.Count);
        Assert.Equal(42, storedQuota.TightestRemainingPercent);
        Assert.Equal("4.20", storedQuota.CreditBalance);
        Assert.NotNull(storedUsage);
        Assert.Equal(1000, storedUsage!.LifetimeTokens);
        Assert.Equal(2, storedUsage.DailyBuckets.Count);
    }

    [Fact]
    public async Task Failed_quota_detail_write_rolls_back_header()
    {
        await using var fixture = await TempStorage.CreateAsync();
        var account = Account("a", Path.Combine(fixture.Root, "profiles", "a"));
        await fixture.Repository.CreateAccountAsync(account);
        var invalid = new QuotaSnapshot(account.Id, new[]
        {
            new QuotaBucket("codex", null, QuotaBucketSlot.Primary, 20, TimeSpan.FromMinutes(-1), null)
        }, DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<StorageException>(() => fixture.Repository.AppendQuotaSnapshotAsync(invalid));

        Assert.Empty(await fixture.Repository.GetQuotaHistoryAsync(account.Id));
    }

    [Fact]
    public async Task Sticky_route_survives_repository_and_database_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-router-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "router.db");
        try
        {
            var firstDatabase = new StorageDatabase(new StorageOptions(path));
            await firstDatabase.InitializeAsync();
            var first = new RouterRepository(firstDatabase);
            var account = Account("a", Path.Combine(root, "profiles", "a"));
            await first.CreateAccountAsync(account);
            var route = new ThreadRoute(new ThreadId("thread-1"), account.Id, new WorkerId("worker-a"),
                RouteReason.AutoQuota, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await first.InsertThreadRouteAsync(route);

            var secondDatabase = new StorageDatabase(new StorageOptions(path));
            await secondDatabase.InitializeAsync();
            var second = new RouterRepository(secondDatabase);
            var restored = await second.GetThreadRouteAsync(route.ThreadId);

            Assert.NotNull(restored);
            Assert.Equal(account.Id, restored!.AccountId);
            Assert.Equal("worker-a", restored.WorkerId.Value);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Duplicate_sticky_route_is_rejected_instead_of_silently_reassigned()
    {
        await using var fixture = await TempStorage.CreateAsync();
        var account = Account("a", Path.Combine(fixture.Root, "profiles", "a"));
        await fixture.Repository.CreateAccountAsync(account);
        var route = new ThreadRoute(new ThreadId("thread-1"), account.Id, new WorkerId("worker-a"),
            RouteReason.AutoQuota, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        await fixture.Repository.InsertThreadRouteAsync(route);
        await Assert.ThrowsAsync<StorageException>(() => fixture.Repository.InsertThreadRouteAsync(route));
    }

    [Fact]
    public async Task Health_settings_worker_and_migration_job_state_are_persistent()
    {
        await using var fixture = await TempStorage.CreateAsync();
        var source = Account("a", Path.Combine(fixture.Root, "profiles", "a"));
        var destination = Account("b", Path.Combine(fixture.Root, "profiles", "b"));
        await fixture.Repository.CreateAccountAsync(source);
        await fixture.Repository.CreateAccountAsync(destination);

        await fixture.Repository.AppendHealthEventAsync(new AccountHealth(
            source.Id, AccountHealthState.Draining, DateTimeOffset.UtcNow, "quota low"));
        Assert.Single(await fixture.Repository.GetHealthEventsAsync(source.Id));

        var settings = RouterSettings.Default with
        {
            Mode = RouterMode.Pinned,
            PinnedAccountId = destination.Id,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await fixture.Repository.UpdateRouterSettingsAsync(settings);
        Assert.Equal(destination.Id, (await fixture.Repository.GetRouterSettingsAsync()).PinnedAccountId);

        var sessionId = await fixture.Repository.StartWorkerSessionAsync(
            new WorkerId("worker-a"), source.Id, WorkerState.Starting, 1234, DateTimeOffset.UtcNow);
        Assert.True(await fixture.Repository.FinishWorkerSessionAsync(
            sessionId, WorkerState.Stopped, DateTimeOffset.UtcNow, 0, null));

        var job = new MigrationJobRecord(
            "job-1",
            new ThreadId("thread-1"),
            source.Id,
            destination.Id,
            null,
            MigrationJobStatus.Pending,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null);
        await fixture.Repository.CreateMigrationJobAsync(job);
        Assert.True(await fixture.Repository.TransitionMigrationJobAsync(
            job.Id, MigrationJobStatus.Pending, MigrationJobStatus.Snapshotting, DateTimeOffset.UtcNow));
        Assert.False(await fixture.Repository.TransitionMigrationJobAsync(
            job.Id, MigrationJobStatus.Pending, MigrationJobStatus.Completed, DateTimeOffset.UtcNow));
        Assert.Equal(MigrationJobStatus.Snapshotting, (await fixture.Repository.GetMigrationJobAsync(job.Id))!.Status);
    }

    [Fact]
    public async Task Invalid_pinned_settings_fail_without_partial_update()
    {
        await using var fixture = await TempStorage.CreateAsync();
        var before = await fixture.Repository.GetRouterSettingsAsync();
        var invalid = before with
        {
            Mode = RouterMode.Pinned,
            PinnedAccountId = new AccountId("missing-account"),
            ShortReservePercent = 33,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await Assert.ThrowsAsync<StorageException>(() => fixture.Repository.UpdateRouterSettingsAsync(invalid));

        var after = await fixture.Repository.GetRouterSettingsAsync();
        Assert.Equal(before.Mode, after.Mode);
        Assert.Equal(before.ShortReservePercent, after.ShortReservePercent);
        Assert.Equal(before.PinnedAccountId, after.PinnedAccountId);
    }

    [Fact]
    public async Task Compatibility_report_round_trips()
    {
        await using var fixture = await TempStorage.CreateAsync();
        var report = new CompatibilityReport(
            CompatibilityState.Compatible,
            new BinaryIdentity(Path.Combine(fixture.Root, "codex.exe"), "0.test", new string('c', 64), 1, DateTimeOffset.UtcNow),
            null,
            DateTimeOffset.UtcNow,
            Array.Empty<CompatibilityIssue>(),
            Array.Empty<string>(),
            Array.Empty<string>());

        await fixture.Repository.AppendCompatibilityRunAsync(report);
        var stored = await fixture.Repository.GetLatestCompatibilityRunAsync();

        Assert.NotNull(stored);
        Assert.Equal(report.State, stored!.Report.State);
        Assert.Equal(report.Binary!.Sha256, stored.Report.Binary!.Sha256);
    }

    [Fact]
    public async Task Concurrent_writers_complete_under_WAL_and_busy_timeout()
    {
        await using var fixture = await TempStorage.CreateAsync();
        var account = Account("a", Path.Combine(fixture.Root, "profiles", "a"));
        await fixture.Repository.CreateAccountAsync(account);

        var writes = Enumerable.Range(0, 40).Select(index => Task.Run(async () =>
        {
            await fixture.Repository.AppendHealthEventAsync(new AccountHealth(
                account.Id,
                AccountHealthState.Healthy,
                DateTimeOffset.UtcNow.AddMilliseconds(index),
                $"event-{index}"));
        }));

        await Task.WhenAll(writes);

        var events = await fixture.Repository.GetHealthEventsAsync(account.Id, 100);
        Assert.Equal(40, events.Count);
    }

    private static AccountProfile Account(string id, string home) =>
        new(new AccountId(id), id.ToUpperInvariant(), home, Enabled: true);

    private sealed class TempStorage : IAsyncDisposable
    {
        private TempStorage(string root, StorageDatabase database)
        {
            Root = root;
            Database = database;
            Repository = new RouterRepository(database);
        }

        public string Root { get; }
        public StorageDatabase Database { get; }
        public RouterRepository Repository { get; }

        public static async Task<TempStorage> CreateAsync(TimeSpan? busyTimeout = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"codex-router-storage-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var database = new StorageDatabase(new StorageOptions(Path.Combine(root, "router.db"), busyTimeout));
            await database.InitializeAsync();
            return new TempStorage(root, database);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
            return ValueTask.CompletedTask;
        }
    }
}
