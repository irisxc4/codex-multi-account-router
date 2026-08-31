using System.Text.Json;
using CodexRouter.Domain;
using CodexRouter.Routing;
using CodexRouter.Storage;
using CodexRouter.Workers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexRouter.Routing.Tests;

public sealed class RouterCoordinatorTests
{
    [Fact]
    public async Task Sticky_route_survives_coordinator_restart_and_never_re_evaluates_existing_thread()
    {
        await using var env = await RoutingEnvironment.CreateAsync();
        await env.AddHealthyAccountAsync("a", usedPercent: 10);
        await env.AddHealthyAccountAsync("b", usedPercent: 70);

        var first = new RouterCoordinator(env.Repository, env.Pool);
        var selection = await first.SelectForNewThreadAsync();
        var route = await first.BindNewThreadAsync(new ThreadId("thread-1"), new WorkerId("worker-a"), selection);
        Assert.Equal(new AccountId("a"), route.AccountId);

        await env.Repository.AppendHealthEventAsync(new AccountHealth(
            route.AccountId, AccountHealthState.Draining, DateTimeOffset.UtcNow, "later became low"));

        var restarted = new RouterCoordinator(env.Repository, env.Pool);
        var restored = await restarted.RequireThreadRouteAsync(new ThreadId("thread-1"));
        Assert.Equal(route.AccountId, restored.AccountId);
    }

    [Fact]
    public async Task Persistent_pin_and_temporary_pin_have_explicit_scope()
    {
        await using var env = await RoutingEnvironment.CreateAsync();
        await env.AddHealthyAccountAsync("a", 10);
        await env.AddHealthyAccountAsync("b", 60);
        var coordinator = new RouterCoordinator(env.Repository, env.Pool);

        var settings = await env.Repository.GetRouterSettingsAsync();
        await env.Repository.UpdateRouterSettingsAsync(settings with
        {
            Mode = RouterMode.Pinned,
            PinnedAccountId = new AccountId("b"),
            UpdatedAt = DateTimeOffset.UtcNow
        });
        Assert.Equal(new AccountId("b"), (await coordinator.SelectForNewThreadAsync()).AccountId);

        coordinator.SetTemporaryPin(new AccountId("a"), TimeSpan.FromHours(1));
        Assert.Equal(new AccountId("a"), (await coordinator.SelectForNewThreadAsync()).AccountId);

        coordinator.ClearTemporaryPin();
        Assert.Equal(new AccountId("b"), (await coordinator.SelectForNewThreadAsync()).AccountId);
    }

    [Fact]
    public async Task Router_off_refuses_account_selection()
    {
        await using var env = await RoutingEnvironment.CreateAsync();
        await env.AddHealthyAccountAsync("a", 10);
        var settings = await env.Repository.GetRouterSettingsAsync();
        await env.Repository.UpdateRouterSettingsAsync(settings with
        {
            Mode = RouterMode.Off,
            PinnedAccountId = null,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await Assert.ThrowsAsync<RoutingDisabledException>(() =>
            new RouterCoordinator(env.Repository, env.Pool).SelectForNewThreadAsync());
    }

    [Fact]
    public async Task Decision_audit_is_attached_to_thread_in_same_sticky_commit()
    {
        await using var env = await RoutingEnvironment.CreateAsync();
        await env.AddHealthyAccountAsync("a", 10);
        var coordinator = new RouterCoordinator(env.Repository, env.Pool);

        var selection = await coordinator.SelectForNewThreadAsync();
        var before = await env.Repository.GetRouteDecisionAuditsAsync();
        Assert.Single(before);
        Assert.Null(before[0].ThreadId);

        await coordinator.BindNewThreadAsync(new ThreadId("thread-a"), new WorkerId("worker-a"), selection);
        var after = await env.Repository.GetRouteDecisionAuditsAsync(new ThreadId("thread-a"));

        Assert.Single(after);
        Assert.Equal(selection.DecisionId, after[0].Id);
        using var document = JsonDocument.Parse(after[0].DecisionJson);
        Assert.True(document.RootElement.TryGetProperty("candidates", out _));
    }

    [Fact]
    public async Task Missing_audit_causes_sticky_insert_to_roll_back()
    {
        await using var env = await RoutingEnvironment.CreateAsync();
        await env.AddHealthyAccountAsync("a", 10);
        var now = DateTimeOffset.UtcNow;
        var fakeSelection = new RouteSelection(
            new AccountId("a"), RouteReason.AutoQuota, 1, now,
            new[]
            {
                new CandidateRouteExplanation(new AccountId("a"), true, Array.Empty<string>(), 1,
                    Array.Empty<RouteFactorScore>(), now, false, 0, 0)
            },
            "missing-audit");

        await Assert.ThrowsAsync<StorageException>(() =>
            new RouterCoordinator(env.Repository, env.Pool).BindNewThreadAsync(
                new ThreadId("thread-x"), new WorkerId("worker-x"), fakeSelection));

        Assert.Null(await env.Repository.GetThreadRouteAsync(new ThreadId("thread-x")));
    }

    [Fact]
    public async Task Candidate_explanations_record_rejection_reason_and_quota_freshness()
    {
        await using var env = await RoutingEnvironment.CreateAsync();
        await env.AddHealthyAccountAsync("good", 10);
        await env.AddHealthyAccountAsync("stale", 10, quotaFetchedAt: DateTimeOffset.UtcNow.AddHours(-1));
        var coordinator = new RouterCoordinator(env.Repository, env.Pool);

        var selection = await coordinator.SelectForNewThreadAsync();
        var stale = selection.Candidates.Single(candidate => candidate.AccountId == new AccountId("stale"));

        Assert.False(stale.Eligible);
        Assert.True(stale.QuotaStale);
        Assert.Contains(stale.RejectedReasons, reason => reason.StartsWith("quota:stale", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fork_inherits_source_account_without_re_scoring()
    {
        await using var env = await RoutingEnvironment.CreateAsync();
        await env.AddHealthyAccountAsync("a", 10);
        await env.AddHealthyAccountAsync("b", 70);
        var coordinator = new RouterCoordinator(env.Repository, env.Pool);
        var selection = await coordinator.SelectForNewThreadAsync();
        await coordinator.BindNewThreadAsync(new ThreadId("source"), new WorkerId("worker-a"), selection);

        var fork = await coordinator.BindForkAsync(
            new ThreadId("source"), new ThreadId("fork"), new WorkerId("worker-a"));

        Assert.Equal(selection.AccountId, fork.AccountId);
        Assert.Equal(RouteReason.Sticky, fork.Reason);
    }

    private sealed class RoutingEnvironment : IAsyncDisposable
    {
        private RoutingEnvironment(
            string root,
            StorageDatabase database,
            RouterRepository repository,
            WorkerPool pool)
        {
            Root = root;
            Database = database;
            Repository = repository;
            Pool = pool;
        }

        public string Root { get; }
        public StorageDatabase Database { get; }
        public RouterRepository Repository { get; }
        public WorkerPool Pool { get; }

        public static async Task<RoutingEnvironment> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"codex-router-routing-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var database = new StorageDatabase(new StorageOptions(Path.Combine(root, "router.db")));
            await database.InitializeAsync();
            var repository = new RouterRepository(database);
            var report = new CompatibilityReport(
                CompatibilityState.Compatible,
                new BinaryIdentity(Path.Combine(root, "codex.exe"), "0.test", new string('a', 64), 1, DateTimeOffset.UtcNow),
                null,
                DateTimeOffset.UtcNow,
                Array.Empty<CompatibilityIssue>(),
                Array.Empty<string>(),
                Array.Empty<string>());
            await repository.AppendCompatibilityRunAsync(report);
            var pool = new WorkerPool(new NeverUsedWorkerFactory(), new WorkerPoolOptions(
                MaxResidentWorkers: 5,
                IdleTtl: TimeSpan.FromHours(1),
                MaintenanceInterval: TimeSpan.FromHours(1)));
            return new RoutingEnvironment(root, database, repository, pool);
        }

        public async Task AddHealthyAccountAsync(
            string id,
            int usedPercent,
            DateTimeOffset? quotaFetchedAt = null)
        {
            var accountId = new AccountId(id);
            var profile = new AccountProfile(accountId, id.ToUpperInvariant(), Path.Combine(Root, "profiles", id));
            await Repository.CreateAccountAsync(profile);
            var fetched = quotaFetchedAt ?? DateTimeOffset.UtcNow;
            await Repository.AppendQuotaSnapshotAsync(new QuotaSnapshot(accountId, new[]
            {
                new QuotaBucket("codex", "Codex", QuotaBucketSlot.Primary, usedPercent,
                    TimeSpan.FromHours(5), DateTimeOffset.UtcNow.AddHours(2)),
                new QuotaBucket("codex", "Codex", QuotaBucketSlot.Secondary, usedPercent,
                    TimeSpan.FromDays(7), DateTimeOffset.UtcNow.AddDays(3))
            }, fetched));
            await Repository.AppendHealthEventAsync(new AccountHealth(accountId, AccountHealthState.Healthy, DateTimeOffset.UtcNow));
        }

        public async ValueTask DisposeAsync()
        {
            await Pool.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class NeverUsedWorkerFactory : IAppServerWorkerFactory
    {
        public IAppServerWorker Create(AccountProfile profile) =>
            throw new InvalidOperationException("Routing coordinator tests must not start workers.");
    }
}
