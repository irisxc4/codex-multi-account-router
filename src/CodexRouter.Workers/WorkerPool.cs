using CodexRouter.Domain;

namespace CodexRouter.Workers;

public sealed record WorkerPoolOptions(
    int MaxResidentWorkers = 3,
    TimeSpan? IdleTtl = null,
    TimeSpan? MaintenanceInterval = null,
    int CrashThreshold = 3,
    TimeSpan? CrashWindow = null,
    TimeSpan? QuarantineDuration = null,
    TimeSpan? InitialRestartBackoff = null,
    TimeSpan? MaxRestartBackoff = null)
{
    public TimeSpan EffectiveIdleTtl => IdleTtl ?? TimeSpan.FromMinutes(15);
    public TimeSpan EffectiveMaintenanceInterval => MaintenanceInterval ?? TimeSpan.FromMinutes(1);
    public TimeSpan EffectiveCrashWindow => CrashWindow ?? TimeSpan.FromMinutes(2);
    public TimeSpan EffectiveQuarantineDuration => QuarantineDuration ?? TimeSpan.FromMinutes(5);
    public TimeSpan EffectiveInitialRestartBackoff => InitialRestartBackoff ?? TimeSpan.FromMilliseconds(250);
    public TimeSpan EffectiveMaxRestartBackoff => MaxRestartBackoff ?? TimeSpan.FromSeconds(15);
}

public sealed record WorkerPoolEntrySnapshot(
    AccountId AccountId,
    WorkerId? WorkerId,
    WorkerState State,
    int ActiveLeases,
    DateTimeOffset LastUsedAt,
    int RecentCrashCount,
    DateTimeOffset? BackoffUntil,
    DateTimeOffset? QuarantineUntil,
    int? ProcessId);

public sealed class WorkerQuarantinedException : Exception
{
    public WorkerQuarantinedException(AccountId accountId, DateTimeOffset until)
        : base($"Worker for account {accountId} is quarantined until {until:O}.")
    {
        AccountId = accountId;
        Until = until;
    }

    public AccountId AccountId { get; }
    public DateTimeOffset Until { get; }
}

public interface IAppServerWorkerFactory
{
    IAppServerWorker Create(AccountProfile profile);
}

public sealed class CodexAppServerWorkerFactory : IAppServerWorkerFactory
{
    private readonly string _codexExecutable;
    private readonly WorkerStartOptions _startOptions;
    private readonly WorkerClientContext? _clientContext;
    private long _sequence;

    public CodexAppServerWorkerFactory(
        string codexExecutable,
        WorkerStartOptions? startOptions = null,
        WorkerClientContext? clientContext = null)
    {
        _codexExecutable = Path.GetFullPath(codexExecutable);
        _startOptions = startOptions ?? new WorkerStartOptions();
        _clientContext = clientContext;
    }

    public IAppServerWorker Create(AccountProfile profile)
    {
        var workerId = new WorkerId($"{profile.Id.Value}-{Interlocked.Increment(ref _sequence)}");
        var environment = ProfileWorkerNetworkRoute.LoadEnvironment(profile.CodexHome);
        var launch = new WorkerLaunchSpec(
            workerId,
            profile.Id,
            _codexExecutable,
            new[] { "app-server" },
            profile.CodexHome,
            ExtraEnvironment: environment);
        return new AppServerWorker(
            launch,
            _clientContext?.Apply(_startOptions) ?? _startOptions);
    }
}

public interface IWorkerPoolClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemWorkerPoolClock : IWorkerPoolClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public sealed class WorkerLease : IAsyncDisposable, IDisposable
{
    private WorkerPool? _owner;

    internal WorkerLease(WorkerPool owner, AccountId accountId, IAppServerWorker worker)
    {
        _owner = owner;
        AccountId = accountId;
        Worker = worker;
    }

    public AccountId AccountId { get; }
    public IAppServerWorker Worker { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _owner, null)?.Release(AccountId);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed record WorkerReadyEvent(AccountId AccountId, IAppServerWorker Worker, DateTimeOffset ReadyAt);

public sealed class WorkerPool : IAsyncDisposable
{
    private readonly IAppServerWorkerFactory _factory;
    private readonly WorkerPoolOptions _options;
    private readonly IWorkerPoolClock _clock;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _maintenanceCts = new();
    private readonly Task _maintenanceTask;
    private bool _disposed;

    public event EventHandler<WorkerReadyEvent>? WorkerReady;

    public WorkerPool(
        IAppServerWorkerFactory factory,
        WorkerPoolOptions? options = null,
        IWorkerPoolClock? clock = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _options = options ?? new WorkerPoolOptions();
        _clock = clock ?? new SystemWorkerPoolClock();
        ValidateOptions(_options);
        _maintenanceTask = Task.Run(MaintenanceLoopAsync, CancellationToken.None);
    }

    public async Task<WorkerLease> AcquireAsync(AccountProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Entry entry;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_entries.TryGetValue(profile.Id.Value, out entry!))
            {
                entry = new Entry(profile, _clock.UtcNow);
                _entries.Add(profile.Id.Value, entry);
            }
            else
            {
                entry.Profile = profile;
            }
        }
        finally
        {
            _gate.Release();
        }

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.UtcNow;
            entry.PruneCrashes(now, _options.EffectiveCrashWindow);
            if (entry.QuarantineUntil is { } quarantineUntil)
            {
                if (quarantineUntil > now)
                {
                    throw new WorkerQuarantinedException(profile.Id, quarantineUntil);
                }
                entry.QuarantineUntil = null;
                entry.Crashes.Clear();
            }

            if (entry.Worker is null || entry.Worker.State is WorkerState.Crashed or WorkerState.Failed or WorkerState.Stopped)
            {
                await ReplaceAndStartWorkerAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            else if (entry.Worker.State is not (WorkerState.Ready or WorkerState.Busy or WorkerState.Draining))
            {
                await entry.Worker.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            var readyWorker = entry.Worker ?? throw new InvalidOperationException("Worker start completed without a worker instance.");
            entry.ActiveLeases++;
            entry.LastUsedAt = _clock.UtcNow;
            return new WorkerLease(this, profile.Id, readyWorker);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task SweepAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        List<Entry> entries;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            entries = _entries.Values.ToList();
        }
        finally
        {
            _gate.Release();
        }

        var now = _clock.UtcNow;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                entry.PruneCrashes(now, _options.EffectiveCrashWindow);
                if (entry.Worker is not null &&
                    entry.ActiveLeases == 0 &&
                    now - entry.LastUsedAt >= _options.EffectiveIdleTtl &&
                    entry.Worker.State is not (WorkerState.Stopped or WorkerState.Stopping))
                {
                    await entry.Worker.StopAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                entry.Gate.Release();
            }
        }

        await EnforceResidentLimitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> EvictAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Entry? entry;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _entries.TryGetValue(accountId.Value, out entry);
        }
        finally
        {
            _gate.Release();
        }

        if (entry is null)
        {
            return true;
        }

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (entry.Sync)
            {
                if (entry.ActiveLeases > 0)
                {
                    return false;
                }
            }

            if (entry.Worker is not null)
            {
                entry.Worker.StateChanged -= OnWorkerStateChanged;
                await entry.Worker.DisposeAsync().ConfigureAwait(false);
                entry.Worker = null;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _entries.Remove(accountId.Value);
            }
            finally
            {
                _gate.Release();
            }
            return true;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task<IReadOnlyList<WorkerPoolEntrySnapshot>> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        List<Entry> entries;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            entries = _entries.Values.ToList();
        }
        finally
        {
            _gate.Release();
        }

        var now = _clock.UtcNow;
        var snapshots = new List<WorkerPoolEntrySnapshot>(entries.Count);
        foreach (var entry in entries)
        {
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                entry.PruneCrashes(now, _options.EffectiveCrashWindow);
                snapshots.Add(new WorkerPoolEntrySnapshot(
                    entry.Profile.Id,
                    entry.Worker?.WorkerId,
                    entry.Worker?.State ?? WorkerState.Stopped,
                    entry.ActiveLeases,
                    entry.LastUsedAt,
                    entry.Crashes.Count,
                    entry.BackoffUntil,
                    entry.QuarantineUntil,
                    entry.Worker?.ProcessId));
            }
            finally
            {
                entry.Gate.Release();
            }
        }
        return snapshots.OrderBy(static snapshot => snapshot.AccountId.Value, StringComparer.Ordinal).ToArray();
    }

    internal void Release(AccountId accountId)
    {
        if (!_entries.TryGetValue(accountId.Value, out var entry))
        {
            return;
        }

        lock (entry.Sync)
        {
            if (entry.ActiveLeases > 0)
            {
                entry.ActiveLeases--;
            }
            entry.LastUsedAt = _clock.UtcNow;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _maintenanceCts.Cancel();
        try { await _maintenanceTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

        List<Entry> entries;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            entries = _entries.Values.ToList();
            _entries.Clear();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var entry in entries)
        {
            await entry.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (entry.Worker is not null)
                {
                    await entry.Worker.DisposeAsync().ConfigureAwait(false);
                    entry.Worker = null;
                }
            }
            finally
            {
                entry.Gate.Release();
                entry.Gate.Dispose();
            }
        }
        _maintenanceCts.Dispose();
        _gate.Dispose();
    }

    private async Task ReplaceAndStartWorkerAsync(Entry entry, CancellationToken cancellationToken)
    {
        if (entry.Worker is not null)
        {
            entry.Worker.StateChanged -= OnWorkerStateChanged;
            await entry.Worker.DisposeAsync().ConfigureAwait(false);
            entry.Worker = null;
        }

        var now = _clock.UtcNow;
        if (entry.Crashes.Count > 0)
        {
            var backoff = ComputeRestartBackoff(entry.Crashes.Count);
            entry.BackoffUntil = now + backoff;
            await _clock.DelayAsync(backoff, cancellationToken).ConfigureAwait(false);
            entry.BackoffUntil = null;
        }

        await ReclaimOneIfAtLimitAsync(entry.Profile.Id, cancellationToken).ConfigureAwait(false);

        var worker = _factory.Create(entry.Profile);
        entry.Worker = worker;
        worker.StateChanged += OnWorkerStateChanged;
        try
        {
            await worker.StartAsync(cancellationToken).ConfigureAwait(false);
            RaiseWorkerReady(new WorkerReadyEvent(entry.Profile.Id, worker, _clock.UtcNow));
        }
        catch
        {
            RegisterCrash(entry, _clock.UtcNow);
            throw;
        }
    }

    private void RaiseWorkerReady(WorkerReadyEvent value)
    {
        var handlers = WorkerReady;
        if (handlers is null)
        {
            return;
        }
        foreach (EventHandler<WorkerReadyEvent> handler in handlers.GetInvocationList())
        {
            try { handler(this, value); } catch { /* Observers cannot break worker startup. */ }
        }
    }

    private void OnWorkerStateChanged(object? sender, WorkerStateChange change)
    {
        if (change.Current != WorkerState.Crashed || !_entries.TryGetValue(change.AccountId.Value, out var entry))
        {
            return;
        }
        RegisterCrash(entry, _clock.UtcNow);
    }

    private void RegisterCrash(Entry entry, DateTimeOffset at)
    {
        lock (entry.Sync)
        {
            entry.Crashes.Enqueue(at);
            entry.PruneCrashes(at, _options.EffectiveCrashWindow);
            if (entry.Crashes.Count >= _options.CrashThreshold)
            {
                entry.QuarantineUntil = at + _options.EffectiveQuarantineDuration;
            }
        }
    }

    private async Task ReclaimOneIfAtLimitAsync(AccountId incomingAccount, CancellationToken cancellationToken)
    {
        List<Entry> candidates;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var resident = _entries.Values.Count(static entry => entry.Worker is { IsAlive: true });
            if (resident < _options.MaxResidentWorkers)
            {
                return;
            }
            candidates = _entries.Values
                .Where(entry => entry.Profile.Id != incomingAccount && entry.Worker is { IsAlive: true })
                .OrderBy(static entry => entry.LastUsedAt)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var candidate in candidates)
        {
            await candidate.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (candidate.ActiveLeases == 0 && candidate.Worker is { IsAlive: true } worker)
                {
                    await worker.StopAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            finally
            {
                candidate.Gate.Release();
            }
        }

        throw new InvalidOperationException(
            $"Worker pool resident limit {_options.MaxResidentWorkers} is reached and all resident workers are active.");
    }

    private async Task EnforceResidentLimitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            List<Entry> resident;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                resident = _entries.Values
                    .Where(static entry => entry.Worker is { IsAlive: true })
                    .OrderBy(static entry => entry.LastUsedAt)
                    .ToList();
            }
            finally
            {
                _gate.Release();
            }

            if (resident.Count <= _options.MaxResidentWorkers)
            {
                return;
            }

            var reclaimed = false;
            foreach (var entry in resident)
            {
                await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (entry.ActiveLeases == 0 && entry.Worker is { IsAlive: true } worker)
                    {
                        await worker.StopAsync(cancellationToken).ConfigureAwait(false);
                        reclaimed = true;
                        break;
                    }
                }
                finally
                {
                    entry.Gate.Release();
                }
            }
            if (!reclaimed)
            {
                return;
            }
        }
    }

    private async Task MaintenanceLoopAsync()
    {
        using var timer = new PeriodicTimer(_options.EffectiveMaintenanceInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_maintenanceCts.Token).ConfigureAwait(false))
            {
                try { await SweepAsync(_maintenanceCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_maintenanceCts.IsCancellationRequested) { break; }
                catch { /* Diagnostics module will observe pool snapshots; maintenance must never kill Router. */ }
            }
        }
        catch (OperationCanceledException) when (_maintenanceCts.IsCancellationRequested)
        {
            // Normal disposal.
        }
    }

    private TimeSpan ComputeRestartBackoff(int crashCount)
    {
        var milliseconds = _options.EffectiveInitialRestartBackoff.TotalMilliseconds * Math.Pow(2, Math.Max(0, crashCount - 1));
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, _options.EffectiveMaxRestartBackoff.TotalMilliseconds));
    }

    private static void ValidateOptions(WorkerPoolOptions options)
    {
        if (options.MaxResidentWorkers < 1) throw new ArgumentOutOfRangeException(nameof(options.MaxResidentWorkers));
        if (options.CrashThreshold < 1) throw new ArgumentOutOfRangeException(nameof(options.CrashThreshold));
        if (options.EffectiveIdleTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.IdleTtl));
        if (options.EffectiveMaintenanceInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.MaintenanceInterval));
        if (options.EffectiveCrashWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.CrashWindow));
        if (options.EffectiveQuarantineDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.QuarantineDuration));
    }

    private sealed class Entry
    {
        public Entry(AccountProfile profile, DateTimeOffset now)
        {
            Profile = profile;
            LastUsedAt = now;
        }

        public object Sync { get; } = new();
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public AccountProfile Profile { get; set; }
        public IAppServerWorker? Worker { get; set; }
        public int ActiveLeases { get; set; }
        public DateTimeOffset LastUsedAt { get; set; }
        public Queue<DateTimeOffset> Crashes { get; } = new();
        public DateTimeOffset? BackoffUntil { get; set; }
        public DateTimeOffset? QuarantineUntil { get; set; }

        public void PruneCrashes(DateTimeOffset now, TimeSpan window)
        {
            lock (Sync)
            {
                while (Crashes.Count > 0 && now - Crashes.Peek() > window)
                {
                    Crashes.Dequeue();
                }
            }
        }
    }
}
