using System.Text.Json;
using CodexRouter.Domain;
using CodexRouter.Workers;
using Xunit;

namespace CodexRouter.Workers.Tests;

public sealed class WorkerPoolTests
{
    [Fact]
    public async Task Pool_is_lazy_and_reuses_worker_per_account()
    {
        var factory = new FakeFactory();
        var clock = new FakeClock();
        await using var pool = NewPool(factory, clock);
        Assert.Empty(factory.Created);

        var profile = Profile("a");
        await using (var first = await pool.AcquireAsync(profile))
        {
            Assert.Single(factory.Created);
            Assert.Equal(WorkerState.Ready, first.Worker.State);
        }
        await using (var second = await pool.AcquireAsync(profile))
        {
            Assert.Single(factory.Created);
            Assert.Same(factory.Created[0], second.Worker);
        }
    }

    [Fact]
    public async Task Resident_limit_reclaims_least_recently_used_idle_worker()
    {
        var factory = new FakeFactory();
        var clock = new FakeClock();
        await using var pool = NewPool(factory, clock, maxResident: 2);

        await using (var lease = await pool.AcquireAsync(Profile("a"))) { }
        clock.Advance(TimeSpan.FromSeconds(1));
        await using (var lease = await pool.AcquireAsync(Profile("b"))) { }
        clock.Advance(TimeSpan.FromSeconds(1));
        await using (var lease = await pool.AcquireAsync(Profile("c"))) { }

        Assert.Equal(3, factory.Created.Count);
        Assert.Equal(WorkerState.Stopped, factory.Created[0].State);
        Assert.Equal(WorkerState.Ready, factory.Created[1].State);
        Assert.Equal(WorkerState.Ready, factory.Created[2].State);
    }

    [Fact]
    public async Task Active_worker_is_never_reclaimed_to_make_room()
    {
        var factory = new FakeFactory();
        var clock = new FakeClock();
        await using var pool = NewPool(factory, clock, maxResident: 1);
        await using var active = await pool.AcquireAsync(Profile("a"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pool.AcquireAsync(Profile("b")));
        Assert.Contains("all resident workers are active", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkerState.Ready, active.Worker.State);
    }

    [Fact]
    public async Task Idle_ttl_stops_unused_worker()
    {
        var factory = new FakeFactory();
        var clock = new FakeClock();
        await using var pool = NewPool(factory, clock, idleTtl: TimeSpan.FromMinutes(5));

        await using (var lease = await pool.AcquireAsync(Profile("a"))) { }
        clock.Advance(TimeSpan.FromMinutes(6));
        await pool.SweepAsync();

        Assert.Equal(WorkerState.Stopped, factory.Created.Single().State);
    }

    [Fact]
    public async Task Crash_backoff_restarts_then_repeated_crash_quarantines_account()
    {
        var factory = new FakeFactory();
        var clock = new FakeClock();
        await using var pool = new WorkerPool(factory, new WorkerPoolOptions(
            MaxResidentWorkers: 2,
            IdleTtl: TimeSpan.FromHours(1),
            MaintenanceInterval: TimeSpan.FromHours(1),
            CrashThreshold: 2,
            CrashWindow: TimeSpan.FromMinutes(2),
            QuarantineDuration: TimeSpan.FromMinutes(5),
            InitialRestartBackoff: TimeSpan.FromMilliseconds(100),
            MaxRestartBackoff: TimeSpan.FromSeconds(1)), clock);

        await using (var lease = await pool.AcquireAsync(Profile("a")))
        {
            ((FakeWorker)lease.Worker).Crash("first");
        }

        await using (var lease = await pool.AcquireAsync(Profile("a")))
        {
            Assert.Equal(2, factory.Created.Count);
            Assert.Contains(TimeSpan.FromMilliseconds(100), clock.Delays);
            ((FakeWorker)lease.Worker).Crash("second");
        }

        var snapshot = (await pool.SnapshotAsync()).Single();
        Assert.Equal(2, snapshot.RecentCrashCount);
        Assert.NotNull(snapshot.QuarantineUntil);
        await Assert.ThrowsAsync<WorkerQuarantinedException>(() => pool.AcquireAsync(Profile("a")));
    }

    [Fact]
    public async Task Quarantine_expires_and_worker_can_recover()
    {
        var factory = new FakeFactory();
        var clock = new FakeClock();
        await using var pool = new WorkerPool(factory, new WorkerPoolOptions(
            MaxResidentWorkers: 2,
            IdleTtl: TimeSpan.FromHours(1),
            MaintenanceInterval: TimeSpan.FromHours(1),
            CrashThreshold: 1,
            CrashWindow: TimeSpan.FromMinutes(1),
            QuarantineDuration: TimeSpan.FromMinutes(2),
            InitialRestartBackoff: TimeSpan.Zero,
            MaxRestartBackoff: TimeSpan.Zero), clock);

        await using (var lease = await pool.AcquireAsync(Profile("a")))
        {
            ((FakeWorker)lease.Worker).Crash("boom");
        }
        await Assert.ThrowsAsync<WorkerQuarantinedException>(() => pool.AcquireAsync(Profile("a")));

        clock.Advance(TimeSpan.FromMinutes(3));
        await using var recovered = await pool.AcquireAsync(Profile("a"));
        Assert.Equal(WorkerState.Ready, recovered.Worker.State);
    }

    [Fact]
    public async Task Dispose_stops_and_disposes_every_worker()
    {
        var factory = new FakeFactory();
        var clock = new FakeClock();
        var pool = NewPool(factory, clock, maxResident: 3);
        await using (var a = await pool.AcquireAsync(Profile("a"))) { }
        await using (var b = await pool.AcquireAsync(Profile("b"))) { }

        await pool.DisposeAsync();

        Assert.Equal(2, factory.Created.Count);
        Assert.All(factory.Created, worker => Assert.True(worker.Disposed));
        Assert.All(factory.Created, worker => Assert.Equal(WorkerState.Stopped, worker.State));
    }

    private static WorkerPool NewPool(
        FakeFactory factory,
        FakeClock clock,
        int maxResident = 3,
        TimeSpan? idleTtl = null) =>
        new(factory, new WorkerPoolOptions(
            MaxResidentWorkers: maxResident,
            IdleTtl: idleTtl ?? TimeSpan.FromHours(1),
            MaintenanceInterval: TimeSpan.FromHours(1),
            CrashThreshold: 3,
            CrashWindow: TimeSpan.FromMinutes(2),
            QuarantineDuration: TimeSpan.FromMinutes(5),
            InitialRestartBackoff: TimeSpan.FromMilliseconds(100),
            MaxRestartBackoff: TimeSpan.FromSeconds(1)), clock);

    private static AccountProfile Profile(string id) =>
        new(new AccountId(id), id.ToUpperInvariant(), Path.Combine(Path.GetTempPath(), "codex-router-pool", id));

    private sealed class FakeClock : IWorkerPoolClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 8, 16, 1, 0, 0, TimeSpan.Zero);
        public List<TimeSpan> Delays { get; } = new();
        public void Advance(TimeSpan duration) => UtcNow += duration;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            UtcNow += delay;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFactory : IAppServerWorkerFactory
    {
        private int _sequence;
        public List<FakeWorker> Created { get; } = new();
        public IAppServerWorker Create(AccountProfile profile)
        {
            var worker = new FakeWorker(new WorkerId($"{profile.Id.Value}-{++_sequence}"), profile.Id);
            Created.Add(worker);
            return worker;
        }
    }

    private sealed class FakeWorker : IAppServerWorker
    {
        public FakeWorker(WorkerId workerId, AccountId accountId)
        {
            WorkerId = workerId;
            AccountId = accountId;
        }

        public WorkerId WorkerId { get; }
        public AccountId AccountId { get; }
        public WorkerState State { get; private set; } = WorkerState.Stopped;
        public int? ProcessId => State is WorkerState.Ready or WorkerState.Busy ? 123 : null;
        public bool IsAlive => State is WorkerState.Ready or WorkerState.Busy or WorkerState.Draining;
        public bool Disposed { get; private set; }
        public event EventHandler<WorkerStateChange>? StateChanged;
        public event EventHandler<WorkerNotification>? NotificationReceived
        {
            add { }
            remove { }
        }
        public event EventHandler<WorkerServerRequest>? ServerRequestReceived
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Change(WorkerState.Ready, null);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Change(WorkerState.Stopped, null);
            return Task.CompletedTask;
        }

        public void Crash(string reason) => Change(WorkerState.Crashed, reason);

        public Task<JsonElement> SendRequestAsync(string method, object? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<JsonElement> SendRetryableRequestAsync(string method, object? parameters, DateTimeOffset deadline, bool retryable, RetryPolicy? policy = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task SendNotificationAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task RespondToServerRequestAsync(WorkerServerRequest request, object? result = null, RpcErrorPayload? error = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public async IAsyncEnumerable<WorkerNotification> ReadNotificationsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public async IAsyncEnumerable<WorkerServerRequest> ReadServerRequestsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public IReadOnlyList<string> GetRecentStderr() => Array.Empty<string>();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            Change(WorkerState.Stopped, null);
            return ValueTask.CompletedTask;
        }

        private void Change(WorkerState next, string? reason)
        {
            var previous = State;
            State = next;
            StateChanged?.Invoke(this, new WorkerStateChange(
                WorkerId, AccountId, previous, next, reason, DateTimeOffset.UtcNow));
        }
    }
}
