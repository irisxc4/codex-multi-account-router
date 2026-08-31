using System.Text.Json;
using CodexRouter.Domain;
using CodexRouter.Protocol;
using CodexRouter.Workers;
using Xunit;

namespace CodexRouter.Workers.Tests;

public sealed class AppServerWorkerTests
{
    [Fact]
    public async Task Worker_initializes_and_correlates_concurrent_requests()
    {
        var root = WorkerTestHelpers.CreateTempRoot("worker-concurrency");
        try
        {
            await using var worker = new AppServerWorker(WorkerTestHelpers.FakeLaunch(root));
            await worker.StartAsync();
            Assert.Equal(WorkerState.Ready, worker.State);
            Assert.True(worker.IsAlive);

            var tasks = Enumerable.Range(0, 30)
                .Select(index => worker.SendRequestAsync("echo", new { index }))
                .ToArray();
            var results = await Task.WhenAll(tasks);

            Assert.Equal(30, results.Length);
            for (var index = 0; index < results.Length; index++)
            {
                Assert.Equal(index, results[index].GetProperty("index").GetInt32());
            }

            await worker.StopAsync();
            Assert.Equal(WorkerState.Stopped, worker.State);
            Assert.Null(worker.ProcessId);
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Notification_channel_preserves_worker_ownership()
    {
        var root = WorkerTestHelpers.CreateTempRoot("worker-notification");
        try
        {
            await using var worker = new AppServerWorker(WorkerTestHelpers.FakeLaunch(root));
            await worker.StartAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var readTask = ReadOneNotificationAsync(worker, cts.Token);

            _ = await worker.SendRequestAsync("emit-notification");
            var notification = await readTask;

            Assert.Equal(worker.WorkerId, notification.WorkerId);
            Assert.Equal(worker.AccountId, notification.AccountId);
            Assert.Equal("fake/notification", notification.Method);
            Assert.Equal(7, notification.Parameters.GetProperty("value").GetInt32());
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Server_request_round_trip_uses_original_id_and_worker()
    {
        var root = WorkerTestHelpers.CreateTempRoot("worker-server-request");
        try
        {
            await using var worker = new AppServerWorker(WorkerTestHelpers.FakeLaunch(root, "server-request"));
            await worker.StartAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var request = await ReadOneServerRequestAsync(worker, cts.Token);
            Assert.Equal("srv-1", request.Id.GetString());
            Assert.Equal("fake/approval", request.Method);
            await worker.RespondToServerRequestAsync(request, new { decision = "accept" }, cancellationToken: cts.Token);

            var notification = await ReadNotificationMatchingAsync(worker, "fake/server-request-completed", cts.Token);
            Assert.True(notification.Parameters.GetProperty("ok").GetBoolean());
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Initialize_timeout_kills_child_and_leaves_no_process_handle()
    {
        var root = WorkerTestHelpers.CreateTempRoot("worker-init-timeout");
        try
        {
            await using var worker = new AppServerWorker(
                WorkerTestHelpers.FakeLaunch(root, "init-timeout"),
                new WorkerStartOptions(InitializeTimeout: TimeSpan.FromMilliseconds(250), StopTimeout: TimeSpan.FromMilliseconds(250)));

            await Assert.ThrowsAsync<TimeoutException>(() => worker.StartAsync());
            Assert.False(worker.IsAlive);
            Assert.Null(worker.ProcessId);
            Assert.Equal(WorkerState.Failed, worker.State);
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Child_exit_during_initialize_is_reported_and_cleaned_up()
    {
        var root = WorkerTestHelpers.CreateTempRoot("worker-init-exit");
        try
        {
            await using var worker = new AppServerWorker(
                WorkerTestHelpers.FakeLaunch(root, "init-exit"),
                new WorkerStartOptions(InitializeTimeout: TimeSpan.FromSeconds(2)));

            await Assert.ThrowsAnyAsync<Exception>(() => worker.StartAsync());
            Assert.False(worker.IsAlive);
            Assert.Null(worker.ProcessId);
            Assert.Contains(worker.State, new[] { WorkerState.Crashed, WorkerState.Failed });
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Malformed_stdout_crashes_worker_instead_of_desynchronizing_protocol()
    {
        var root = WorkerTestHelpers.CreateTempRoot("worker-malformed");
        try
        {
            await using var worker = new AppServerWorker(WorkerTestHelpers.FakeLaunch(root, "malformed"));
            await worker.StartAsync();

            await WaitForStateAsync(worker, WorkerState.Crashed, TimeSpan.FromSeconds(3));
            Assert.Equal(WorkerState.Crashed, worker.State);
            Assert.False(worker.IsAlive);
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Stderr_flood_is_drained_and_bounded()
    {
        var root = WorkerTestHelpers.CreateTempRoot("worker-stderr");
        try
        {
            await using var worker = new AppServerWorker(WorkerTestHelpers.FakeLaunch(root, "stderr-flood"));
            await worker.StartAsync();
            _ = await worker.SendRequestAsync("echo", new { value = 1 });

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (worker.GetRecentStderr().Count < 500 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            var stderr = worker.GetRecentStderr();
            Assert.InRange(stderr.Count, 1, 512);
            Assert.Contains(stderr, line => line.Contains("stderr-1499", StringComparison.Ordinal));
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Retryable_overload_uses_bounded_backoff_and_eventually_succeeds()
    {
        var root = WorkerTestHelpers.CreateTempRoot("worker-retry");
        try
        {
            var scheduler = new FakeRetryScheduler(DateTimeOffset.UtcNow);
            await using var worker = new AppServerWorker(WorkerTestHelpers.FakeLaunch(root), retryScheduler: scheduler);
            await worker.StartAsync();

            var result = await worker.SendRetryableRequestAsync(
                "overload",
                null,
                scheduler.UtcNow.AddSeconds(10),
                retryable: true,
                new RetryPolicy(MaxAttempts: 4, InitialDelay: TimeSpan.FromMilliseconds(100), MaxDelay: TimeSpan.FromMilliseconds(500), JitterRatio: 0));

            Assert.Equal(3, result.GetProperty("attempts").GetInt32());
            Assert.Equal(new[] { TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200) }, scheduler.Delays);
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Non_retryable_overload_is_not_replayed()
    {
        var root = WorkerTestHelpers.CreateTempRoot("worker-nonretry");
        try
        {
            var scheduler = new FakeRetryScheduler(DateTimeOffset.UtcNow);
            await using var worker = new AppServerWorker(WorkerTestHelpers.FakeLaunch(root), retryScheduler: scheduler);
            await worker.StartAsync();

            var error = await Assert.ThrowsAsync<AppServerRpcException>(() => worker.SendRetryableRequestAsync(
                "overload",
                null,
                scheduler.UtcNow.AddSeconds(5),
                retryable: false,
                cancellationToken: CancellationToken.None));

            Assert.Equal(-32001, error.Code);
            Assert.Empty(scheduler.Delays);
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Request_timeout_does_not_poison_future_correlation()
    {
        var root = WorkerTestHelpers.CreateTempRoot("worker-request-timeout");
        try
        {
            await using var worker = new AppServerWorker(WorkerTestHelpers.FakeLaunch(root));
            await worker.StartAsync();

            await Assert.ThrowsAsync<TimeoutException>(() => worker.SendRequestAsync(
                "hang", null, TimeSpan.FromMilliseconds(150)));
            var result = await worker.SendRequestAsync("echo", new { ok = true });
            Assert.True(result.GetProperty("ok").GetBoolean());
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Worker_sets_isolated_codex_home_and_strips_router_cli_override_from_child()
    {
        var root = WorkerTestHelpers.CreateTempRoot("worker-environment");
        try
        {
            var launch = WorkerTestHelpers.FakeLaunch(root) with
            {
                ExtraEnvironment = new Dictionary<string, string?>
                {
                    ["CODEX_CLI_PATH"] = Path.Combine(root, "codex-route.exe")
                }
            };
            await using var worker = new AppServerWorker(launch);
            await worker.StartAsync();

            var environment = await worker.SendRequestAsync("fake/environment", null, TimeSpan.FromSeconds(5));

            Assert.Equal(Path.GetFullPath(launch.CodexHome), Path.GetFullPath(environment.GetProperty("codexHome").GetString()!));
            Assert.Equal(JsonValueKind.Null, environment.GetProperty("codexCliPath").ValueKind);
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Real_local_codex_app_server_initializes_in_isolated_home()
    {
        var discovery = await new CodexBinaryDiscovery().DiscoverAsync();
        Assert.True(discovery.Succeeded, discovery.Error);
        var root = WorkerTestHelpers.CreateTempRoot("worker-real-codex");
        try
        {
            var home = Path.Combine(root, "codex-home");
            Directory.CreateDirectory(home);
            await File.WriteAllTextAsync(Path.Combine(home, "config.toml"), "cli_auth_credentials_store = \"keyring\"\n");
            await using var worker = new AppServerWorker(
                WorkerLaunchSpec.ForCodex(new WorkerId("real-codex"), new AccountId("real"), discovery.Binary!.Path, home),
                new WorkerStartOptions(InitializeTimeout: TimeSpan.FromSeconds(15), StopTimeout: TimeSpan.FromSeconds(5)));

            await worker.StartAsync();
            Assert.Equal(WorkerState.Ready, worker.State);
            var accountRead = await worker.SendRequestAsync("account/read", new { refreshToken = false }, TimeSpan.FromSeconds(10));
            Assert.Equal(JsonValueKind.Object, accountRead.ValueKind);
            await worker.StopAsync();
            Assert.False(worker.IsAlive);
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    private static async Task<WorkerNotification> ReadOneNotificationAsync(IAppServerWorker worker, CancellationToken cancellationToken)
    {
        await foreach (var item in worker.ReadNotificationsAsync(cancellationToken))
        {
            return item;
        }
        throw new InvalidOperationException("Notification stream completed unexpectedly.");
    }

    private static async Task<WorkerNotification> ReadNotificationMatchingAsync(
        IAppServerWorker worker,
        string method,
        CancellationToken cancellationToken)
    {
        await foreach (var item in worker.ReadNotificationsAsync(cancellationToken))
        {
            if (item.Method == method)
            {
                return item;
            }
        }
        throw new InvalidOperationException("Notification stream completed unexpectedly.");
    }

    private static async Task<WorkerServerRequest> ReadOneServerRequestAsync(IAppServerWorker worker, CancellationToken cancellationToken)
    {
        await foreach (var item in worker.ReadServerRequestsAsync(cancellationToken))
        {
            return item;
        }
        throw new InvalidOperationException("Server-request stream completed unexpectedly.");
    }

    private static async Task WaitForStateAsync(IAppServerWorker worker, WorkerState state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (worker.State == state)
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.Equal(state, worker.State);
    }

    private sealed class FakeRetryScheduler : IRetryScheduler
    {
        public FakeRetryScheduler(DateTimeOffset now) => UtcNow = now;
        public DateTimeOffset UtcNow { get; private set; }
        public List<TimeSpan> Delays { get; } = new();
        public double NextUnitDouble() => 0.5;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            UtcNow += delay;
            return Task.CompletedTask;
        }
    }
}
