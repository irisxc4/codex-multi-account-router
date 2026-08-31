using System.Collections.Concurrent;
using System.Text.Json;
using CodexRouter.Domain;
using CodexRouter.Migration;
using CodexRouter.Storage;
using CodexRouter.Workers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexRouter.Migration.Tests;

public sealed class ThreadMigrationEngineTests
{
    [Fact]
    public async Task Completed_migration_keeps_source_owner_creates_new_target_thread_and_visible_handoff()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await env.AddAccountsAndSourceRouteAsync();
        await using var engine = env.CreateEngine();

        var result = await engine.StartAsync(new ThreadId("source-thread"), new AccountId("target"));
        var job = await engine.GetAsync(result.JobId);

        Assert.Equal(ThreadMigrationState.Completed, job.State);
        Assert.NotNull(job.TargetThreadId);
        Assert.NotEqual(job.SourceThreadId, job.TargetThreadId);
        Assert.Equal(new AccountId("source"), (await env.Repository.GetThreadRouteAsync(new ThreadId("source-thread")))!.AccountId);
        Assert.Equal(new AccountId("target"), (await env.Repository.GetThreadRouteAsync(job.TargetThreadId!.Value))!.AccountId);
        Assert.Contains("Codex Router migration handoff", env.Factory.Target.SeededText, StringComparison.Ordinal);
        Assert.Contains("visible user task", env.Factory.Target.SeededText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret hidden reasoning", env.Factory.Target.SeededText, StringComparison.Ordinal);

        var events = await env.Repository.GetThreadMigrationEventsAsync(job.Id);
        Assert.Equal(new[] { "pending", "snapshotting", "creating-target", "seeding", "completed" },
            events.Select(static e => e.ToState).ToArray());
    }

    [Fact]
    public async Task Failed_seed_retry_reuses_existing_target_thread_instead_of_creating_ghost_duplicate()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await env.AddAccountsAndSourceRouteAsync();
        env.Factory.Target.FailSeed = true;
        await using var engine = env.CreateEngine();

        var failure = await Assert.ThrowsAsync<ThreadMigrationException>(() =>
            engine.StartAsync(new ThreadId("source-thread"), new AccountId("target")));
        Assert.Contains("failed", failure.Message, StringComparison.OrdinalIgnoreCase);

        var failed = Assert.Single(await engine.ListAsync(new ThreadId("source-thread")));
        Assert.Equal(ThreadMigrationState.Failed, failed.State);
        Assert.NotNull(failed.TargetThreadId);
        Assert.Equal(1, env.Factory.Target.ThreadStartCalls);

        env.Factory.Target.FailSeed = false;
        var retried = await engine.RetryAsync(failed.Id);

        Assert.Equal(ThreadMigrationState.Completed, retried.State);
        Assert.Equal(failed.TargetThreadId, retried.TargetThreadId);
        Assert.Equal(1, env.Factory.Target.ThreadStartCalls);
        Assert.Equal(2, env.Factory.Target.TurnStartCalls);
    }

    [Fact]
    public async Task Retry_after_lost_seed_response_detects_existing_handoff_and_does_not_duplicate_turn()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await env.AddAccountsAndSourceRouteAsync();
        env.Factory.Target.PersistSeedThenThrowOnce = true;
        await using var engine = env.CreateEngine();

        await Assert.ThrowsAsync<ThreadMigrationException>(() =>
            engine.StartAsync(new ThreadId("source-thread"), new AccountId("target")));
        var failed = Assert.Single(await engine.ListAsync(new ThreadId("source-thread")));
        Assert.Equal(ThreadMigrationState.Failed, failed.State);
        Assert.Equal(1, env.Factory.Target.TurnStartCalls);

        var retried = await engine.RetryAsync(failed.Id);

        Assert.Equal(ThreadMigrationState.Completed, retried.State);
        Assert.Equal(1, env.Factory.Target.TurnStartCalls);
        var events = await env.Repository.GetThreadMigrationEventsAsync(failed.Id);
        Assert.Contains(events, item => item.Message?.Contains("without duplicate turn", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Cancellation_is_persisted_and_never_rebinds_source_thread()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await env.AddAccountsAndSourceRouteAsync();
        env.Factory.Target.BlockSeedUntilCanceled = true;
        await using var engine = env.CreateEngine();
        using var cts = new CancellationTokenSource();
        var running = engine.StartAsync(new ThreadId("source-thread"), new AccountId("target"), cts.Token);

        await env.Factory.Target.SeedEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cts.Cancel();
        await Assert.ThrowsAsync<ThreadMigrationCanceledException>(() => running);

        var job = Assert.Single(await engine.ListAsync(new ThreadId("source-thread")));
        Assert.Equal(ThreadMigrationState.Canceled, job.State);
        Assert.Equal(new AccountId("source"), (await env.Repository.GetThreadRouteAsync(new ThreadId("source-thread")))!.AccountId);
    }

    [Fact]
    public async Task Same_account_migration_is_rejected_before_job_creation()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await env.AddAccountsAndSourceRouteAsync();
        await using var engine = env.CreateEngine();

        await Assert.ThrowsAsync<ThreadMigrationException>(() =>
            engine.StartAsync(new ThreadId("source-thread"), new AccountId("source")));
        Assert.Empty(await engine.ListAsync());
    }

    [Fact]
    public async Task Active_source_thread_is_rejected_before_target_creation()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await env.AddAccountsAndSourceRouteAsync();
        env.Factory.Source.SourceThreadStatus = "active";
        await using var engine = env.CreateEngine();

        var error = await Assert.ThrowsAsync<ThreadMigrationException>(() =>
            engine.StartAsync(new ThreadId("source-thread"), new AccountId("target")));

        Assert.Contains("active", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, env.Factory.Target.ThreadStartCalls);
        Assert.Equal(ThreadMigrationState.Failed,
            Assert.Single(await engine.ListAsync(new ThreadId("source-thread"))).State);
    }

    [Fact]
    public async Task In_progress_turn_is_rejected_before_target_creation()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await env.AddAccountsAndSourceRouteAsync();
        env.Factory.Source.SourceTurnStatus = "inProgress";
        await using var engine = env.CreateEngine();

        var error = await Assert.ThrowsAsync<ThreadMigrationException>(() =>
            engine.StartAsync(new ThreadId("source-thread"), new AccountId("target")));

        Assert.Contains("in-progress", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, env.Factory.Target.ThreadStartCalls);
    }

    [Fact]
    public async Task Stale_target_quota_is_rejected_before_job_creation()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await env.AddAccountsAndSourceRouteAsync(DateTimeOffset.UtcNow.AddHours(-1));
        await using var engine = env.CreateEngine();

        var error = await Assert.ThrowsAsync<ThreadMigrationException>(() =>
            engine.StartAsync(new ThreadId("source-thread"), new AccountId("target")));

        Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await engine.ListAsync());
    }

    [Fact]
    public async Task Same_source_thread_cannot_have_two_active_migrations()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await env.AddAccountsAndSourceRouteAsync();
        var secondTargetHome = Path.Combine(env.Root, "target-two-home");
        Directory.CreateDirectory(secondTargetHome);
        await env.Repository.CreateAccountAsync(new AccountProfile(
            new AccountId("target-two"), "Target Two", secondTargetHome));
        var now = DateTimeOffset.UtcNow;
        var first = new StoredThreadMigrationJob(
            "migration-one", new ThreadId("source-thread"), new AccountId("source"), new AccountId("target"),
            null, "pending", null, null, null, now, now, null);
        var second = first with { Id = "migration-two", TargetAccountId = new AccountId("target-two") };

        await env.Repository.CreateThreadMigrationJobAsync(first);
        var error = await Assert.ThrowsAsync<StorageException>(() =>
            env.Repository.CreateThreadMigrationJobAsync(second));

        Assert.Contains("already has active migration", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Snapshot_contains_git_facts_but_does_not_invent_semantic_completion()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await env.AddAccountsAndSourceRouteAsync();
        await using var engine = env.CreateEngine();

        var result = await engine.StartAsync(new ThreadId("source-thread"), new AccountId("target"));
        var job = await engine.GetAsync(result.JobId);
        var snapshot = Assert.IsType<ThreadMigrationSnapshot>(job.Snapshot);

        Assert.Equal("main", snapshot.GitBranch);
        Assert.Equal("abc123", snapshot.GitCommit);
        Assert.Contains("src/Feature.cs", snapshot.RelevantFiles);
        Assert.Contains("does not semantically guess", snapshot.CompletedWork, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Not inferred as fact", snapshot.PendingWork, StringComparison.Ordinal);
        Assert.Contains("visible assistant result", snapshot.RecentVisibleContext, StringComparison.Ordinal);
        Assert.DoesNotContain("secret hidden reasoning", snapshot.RecentVisibleContext, StringComparison.Ordinal);
    }

    private sealed class TestEnvironment : IAsyncDisposable
    {
        private TestEnvironment(string root, StorageDatabase database, RouterRepository repository, FakeWorkerFactory factory, WorkerPool pool)
        {
            Root = root;
            Database = database;
            Repository = repository;
            Factory = factory;
            Pool = pool;
        }

        public string Root { get; }
        public StorageDatabase Database { get; }
        public RouterRepository Repository { get; }
        public FakeWorkerFactory Factory { get; }
        public WorkerPool Pool { get; }

        public static async Task<TestEnvironment> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"codex-router-migration-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var database = new StorageDatabase(new StorageOptions(Path.Combine(root, "router.db")));
            await database.InitializeAsync();
            var repository = new RouterRepository(database);
            var factory = new FakeWorkerFactory();
            var pool = new WorkerPool(factory, new WorkerPoolOptions(
                MaxResidentWorkers: 4,
                IdleTtl: TimeSpan.FromHours(1),
                MaintenanceInterval: TimeSpan.FromHours(1)));
            return new TestEnvironment(root, database, repository, factory, pool);
        }

        public ThreadMigrationEngine CreateEngine() =>
            new(Repository, Pool, new ThreadSnapshotBuilder(new FakeGitSnapshotProvider()));

        public async Task AddAccountsAndSourceRouteAsync(DateTimeOffset? targetQuotaFetchedAt = null)
        {
            var sourceHome = Path.Combine(Root, "source-home");
            var targetHome = Path.Combine(Root, "target-home");
            Directory.CreateDirectory(sourceHome);
            Directory.CreateDirectory(targetHome);
            await Repository.CreateAccountAsync(new AccountProfile(new AccountId("source"), "Source", sourceHome));
            await Repository.CreateAccountAsync(new AccountProfile(new AccountId("target"), "Target", targetHome));
            await Repository.InsertThreadRouteAsync(new ThreadRoute(
                new ThreadId("source-thread"), new AccountId("source"), new WorkerId("source-worker"),
                RouteReason.Sticky, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            var fetchedAt = targetQuotaFetchedAt ?? DateTimeOffset.UtcNow;
            await Repository.AppendQuotaSnapshotAsync(new QuotaSnapshot(
                new AccountId("target"),
                new[]
                {
                    new QuotaBucket("codex", "Codex", QuotaBucketSlot.Primary, 15, TimeSpan.FromHours(5), fetchedAt.AddHours(4)),
                    new QuotaBucket("codex", "Codex", QuotaBucketSlot.Secondary, 20, TimeSpan.FromDays(7), fetchedAt.AddDays(6))
                },
                fetchedAt));
        }

        public async ValueTask DisposeAsync()
        {
            await Pool.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class FakeGitSnapshotProvider : IGitSnapshotProvider
    {
        public Task<GitWorkspaceSnapshot> CaptureAsync(string? cwd, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitWorkspaceSnapshot(
                "main", "abc123", " M src/Feature.cs", "diff --git a/src/Feature.cs b/src/Feature.cs\n+changed",
                new[] { "src/Feature.cs" }));
    }

    private sealed class FakeWorkerFactory : IAppServerWorkerFactory
    {
        private readonly ConcurrentDictionary<string, FakeWorker> _workers = new(StringComparer.Ordinal);
        public FakeWorker Source =>
            _workers.GetOrAdd("source", id => new FakeWorker(new WorkerId($"{id}-worker"), new AccountId(id)));
        public FakeWorker Target =>
            _workers.GetOrAdd("target", id => new FakeWorker(new WorkerId($"{id}-worker"), new AccountId(id)));

        public IAppServerWorker Create(AccountProfile profile) =>
            _workers.GetOrAdd(profile.Id.Value, id => new FakeWorker(new WorkerId($"{id}-worker"), profile.Id));
    }

    private sealed class FakeWorker : IAppServerWorker
    {
        private int _targetThreadSequence;

        public FakeWorker(WorkerId workerId, AccountId accountId)
        {
            WorkerId = workerId;
            AccountId = accountId;
        }

        public WorkerId WorkerId { get; }
        public AccountId AccountId { get; }
        public WorkerState State { get; private set; } = WorkerState.Stopped;
        public int? ProcessId => IsAlive ? 4242 : null;
        public bool IsAlive => State is WorkerState.Ready or WorkerState.Busy or WorkerState.Draining;
        public bool FailSeed { get; set; }
        public bool BlockSeedUntilCanceled { get; set; }
        public bool PersistSeedThenThrowOnce { get; set; }
        public bool PersistedSeed { get; private set; }
        public string SourceThreadStatus { get; set; } = "idle";
        public string SourceTurnStatus { get; set; } = "completed";
        public int ThreadStartCalls { get; private set; }
        public int TurnStartCalls { get; private set; }
        public string SeededText { get; private set; } = string.Empty;
        public TaskCompletionSource<bool> SeedEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<WorkerStateChange>? StateChanged;
        public event EventHandler<WorkerNotification>? NotificationReceived { add { } remove { } }
        public event EventHandler<WorkerServerRequest>? ServerRequestReceived { add { } remove { } }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Change(WorkerState.Ready);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Change(WorkerState.Stopped);
            return Task.CompletedTask;
        }

        public async Task<JsonElement> SendRequestAsync(string method, object? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (method == "thread/read")
            {
                if (AccountId == new AccountId("target") && PersistedSeed)
                {
                    return Parse(JsonSerializer.Serialize(new
                    {
                        thread = new
                        {
                            id = "target-thread-1",
                            cwd = "C:/repo",
                            turns = new[] { new { items = new[] { new { type = "userMessage", text = SeededText } } } }
                        }
                    }));
                }
                return Parse(JsonSerializer.Serialize(new
                {
                    thread = new
                    {
                        id = "source-thread",
                        cwd = "C:/repo",
                        status = new { type = SourceThreadStatus },
                        turns = new[]
                        {
                            new
                            {
                                status = SourceTurnStatus,
                                items = new[]
                                {
                                    new { type = "userMessage", text = "visible user task" },
                                    new { type = "reasoning", text = "secret hidden reasoning" },
                                    new { type = "agentMessage", text = "visible assistant result" }
                                }
                            }
                        }
                    }
                }));
            }
            if (method == "thread/start")
            {
                ThreadStartCalls++;
                var id = $"target-thread-{Interlocked.Increment(ref _targetThreadSequence)}";
                return Parse($"{{\"thread\":{{\"id\":\"{id}\"}}}}");
            }
            if (method == "turn/start")
            {
                TurnStartCalls++;
                var element = ToElement(parameters);
                SeededText = element.GetProperty("input")[0].GetProperty("text").GetString() ?? string.Empty;
                SeedEntered.TrySetResult(true);
                if (BlockSeedUntilCanceled)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                if (PersistSeedThenThrowOnce && !PersistedSeed)
                {
                    PersistedSeed = true;
                    throw new IOException("simulated lost response after server persisted seed");
                }
                if (FailSeed)
                {
                    throw new AppServerRpcException(-32001, "seed overloaded");
                }
                PersistedSeed = true;
                return Parse("{\"turn\":{\"id\":\"seed-turn\",\"status\":\"completed\",\"items\":[]}}" );
            }
            throw new AppServerRpcException(-32601, $"unsupported {method}");
        }

        public Task<JsonElement> SendRetryableRequestAsync(string method, object? parameters, DateTimeOffset deadline, bool retryable, RetryPolicy? policy = null, CancellationToken cancellationToken = default) =>
            SendRequestAsync(method, parameters, deadline - DateTimeOffset.UtcNow, cancellationToken);
        public Task SendNotificationAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RespondToServerRequestAsync(WorkerServerRequest request, object? result = null, RpcErrorPayload? error = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async IAsyncEnumerable<WorkerNotification> ReadNotificationsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<WorkerServerRequest> ReadServerRequestsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
        public IReadOnlyList<string> GetRecentStderr() => Array.Empty<string>();
        public ValueTask DisposeAsync() { Change(WorkerState.Stopped); return ValueTask.CompletedTask; }

        private void Change(WorkerState next)
        {
            var previous = State;
            State = next;
            StateChanged?.Invoke(this, new WorkerStateChange(WorkerId, AccountId, previous, next, null, DateTimeOffset.UtcNow));
        }

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static JsonElement ToElement(object? value)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
            return document.RootElement.Clone();
        }
    }
}
