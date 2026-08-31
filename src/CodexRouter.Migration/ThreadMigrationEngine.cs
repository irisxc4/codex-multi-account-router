using System.Collections.Concurrent;
using System.Text.Json;
using CodexRouter.Domain;
using CodexRouter.Storage;
using CodexRouter.Workers;

namespace CodexRouter.Migration;

public sealed class ThreadMigrationEngine : IAsyncDisposable
{
    private readonly RouterRepository _repository;
    private readonly WorkerPool _workerPool;
    private readonly ThreadSnapshotBuilder _snapshotBuilder;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _backgroundTasks = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _disposeCts = new();
    private int _disposed;

    public ThreadMigrationEngine(
        RouterRepository repository,
        WorkerPool workerPool,
        ThreadSnapshotBuilder? snapshotBuilder = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _workerPool = workerPool ?? throw new ArgumentNullException(nameof(workerPool));
        _snapshotBuilder = snapshotBuilder ?? new ThreadSnapshotBuilder();
    }

    public async Task<ThreadMigrationStartResult> StartAsync(
        ThreadId sourceThreadId,
        AccountId targetAccountId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var job = await CreateJobAsync(sourceThreadId, targetAccountId, cancellationToken).ConfigureAwait(false);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_running.TryAdd(job.Id, linked))
        {
            linked.Dispose();
            throw new ThreadMigrationException($"Migration '{job.Id}' is already running.");
        }
        try
        {
            await ContinueAsync(job.Id, linked.Token).ConfigureAwait(false);
            var completed = await RequireStoredJobAsync(job.Id, cancellationToken).ConfigureAwait(false);
            return new ThreadMigrationStartResult(job.Id, ParseState(completed.State));
        }
        finally
        {
            if (_running.TryRemove(job.Id, out var removed)) removed.Dispose();
        }
    }

    public async Task<ThreadMigrationStartResult> QueueAsync(
        ThreadId sourceThreadId,
        AccountId targetAccountId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var job = await CreateJobAsync(sourceThreadId, targetAccountId, cancellationToken).ConfigureAwait(false);
        StartBackground(job.Id, CancellationToken.None);
        return new ThreadMigrationStartResult(job.Id, ThreadMigrationState.Pending);
    }

    public async Task<ThreadMigrationJob> RetryAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var job = await RequireStoredJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (ParseState(job.State) != ThreadMigrationState.Failed)
        {
            throw new ThreadMigrationException($"Only failed migrations can be retried. Current state: {job.State}.");
        }

        var resumeState = job.SnapshotJson is null
            ? ThreadMigrationState.Snapshotting
            : job.TargetThreadId is null
                ? ThreadMigrationState.CreatingTarget
                : ThreadMigrationState.Seeding;
        await _repository.TransitionThreadMigrationJobAsync(
            job.Id,
            job.State,
            StateText(resumeState),
            error: null,
            message: "retry requested",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_running.TryAdd(job.Id, linked))
        {
            linked.Dispose();
            throw new ThreadMigrationException($"Migration '{job.Id}' is already running.");
        }
        try
        {
            await ContinueAsync(job.Id, linked.Token).ConfigureAwait(false);
            return await GetAsync(job.Id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_running.TryRemove(job.Id, out var removed)) removed.Dispose();
        }
    }

    public async Task<ThreadMigrationStartResult> QueueRetryAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var job = await RequireStoredJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (ParseState(job.State) != ThreadMigrationState.Failed)
        {
            throw new ThreadMigrationException($"Only failed migrations can be retried. Current state: {job.State}.");
        }
        var resumeState = job.SnapshotJson is null
            ? ThreadMigrationState.Snapshotting
            : job.TargetThreadId is null
                ? ThreadMigrationState.CreatingTarget
                : ThreadMigrationState.Seeding;
        await _repository.TransitionThreadMigrationJobAsync(
            job.Id, job.State, StateText(resumeState), error: null, message: "retry queued",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        StartBackground(job.Id, CancellationToken.None);
        return new ThreadMigrationStartResult(job.Id, resumeState);
    }

    public async Task CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_running.TryGetValue(jobId, out var running))
        {
            running.Cancel();
        }
        var job = await RequireStoredJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        var state = ParseState(job.State);
        if (state is ThreadMigrationState.Completed or ThreadMigrationState.Canceled)
        {
            return;
        }
        await TransitionAsync(
            job,
            ThreadMigrationState.Canceled,
            message: "canceled by user",
            markCompleted: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<ThreadMigrationJob> GetAsync(string jobId, CancellationToken cancellationToken = default) =>
        Map(await RequireStoredJobAsync(jobId, cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<ThreadMigrationJob>> ListAsync(
        ThreadId? sourceThreadId = null,
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        (await _repository.ListThreadMigrationJobsAsync(sourceThreadId, limit, cancellationToken).ConfigureAwait(false))
        .Select(Map)
        .ToArray();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _disposeCts.Cancel();
        foreach (var running in _running.Values) running.Cancel();
        var background = _backgroundTasks.Values.ToArray();
        if (background.Length > 0)
        {
            try { await Task.WhenAll(background).ConfigureAwait(false); } catch { }
        }
        foreach (var pair in _running.ToArray())
        {
            if (_running.TryRemove(pair.Key, out var removed)) removed.Dispose();
        }
        _backgroundTasks.Clear();
        _disposeCts.Dispose();
    }

    private async Task<StoredThreadMigrationJob> CreateJobAsync(
        ThreadId sourceThreadId,
        AccountId targetAccountId,
        CancellationToken cancellationToken)
    {
        var sourceRoute = await _repository.GetThreadRouteAsync(sourceThreadId, cancellationToken).ConfigureAwait(false)
            ?? throw new ThreadMigrationException($"Source thread '{sourceThreadId}' has no known sticky owner.");
        if (sourceRoute.AccountId == targetAccountId)
        {
            throw new ThreadMigrationException("Source and target accounts are identical; no migration is needed.");
        }
        var target = await _repository.GetAccountAsync(targetAccountId, cancellationToken).ConfigureAwait(false)
            ?? throw new ThreadMigrationException($"Target account '{targetAccountId}' does not exist.");
        if (!target.Profile.Enabled)
        {
            throw new ThreadMigrationException($"Target account '{targetAccountId}' is disabled.");
        }
        var now = DateTimeOffset.UtcNow;
        await EnsureTargetEligibleAsync(target.Profile.Id, now, cancellationToken).ConfigureAwait(false);
        var job = new StoredThreadMigrationJob(
            $"mig-{Guid.NewGuid():N}", sourceThreadId, sourceRoute.AccountId, targetAccountId, null,
            StateText(ThreadMigrationState.Pending), null, null, null, now, now, null);
        try
        {
            await _repository.CreateThreadMigrationJobAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageException ex)
        {
            throw new ThreadMigrationException(
                $"A migration for source thread '{sourceThreadId}' is already active or could not be created.",
                ex);
        }
        return job;
    }

    private async Task EnsureTargetEligibleAsync(
        AccountId targetAccountId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var settings = await _repository.GetRouterSettingsAsync(cancellationToken).ConfigureAwait(false);
        var health = (await _repository.GetHealthEventsAsync(targetAccountId, 1, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault()?.Health;
        if (health?.State is AccountHealthState.AuthRequired or
            AccountHealthState.Cooldown or
            AccountHealthState.Draining or
            AccountHealthState.Disabled)
        {
            throw new ThreadMigrationException(
                $"Target account '{targetAccountId}' is not available for migration: {health.State} ({health.Reason ?? "no reason"}).");
        }

        var quota = await _repository.GetLatestQuotaSnapshotAsync(targetAccountId, cancellationToken).ConfigureAwait(false);
        if (quota is null)
        {
            throw new ThreadMigrationException($"Target account '{targetAccountId}' has no quota snapshot. Refresh quota and retry.");
        }
        var age = now - quota.FetchedAt;
        if (age > settings.QuotaStaleAfter)
        {
            throw new ThreadMigrationException(
                $"Target account '{targetAccountId}' quota is stale ({age.TotalSeconds:0}s). Refresh quota and retry.");
        }
        if (quota.IsRateLimited || quota.SpendControlReached == true)
        {
            throw new ThreadMigrationException($"Target account '{targetAccountId}' is rate limited.");
        }

        var preferences = await _repository.GetAccountPreferencesAsync(targetAccountId, cancellationToken).ConfigureAwait(false);
        var generalBuckets = quota.Buckets
            .Where(static bucket => string.Equals(bucket.LimitId, "codex", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var effectiveBuckets = generalBuckets.Length > 0 ? generalBuckets : quota.Buckets.ToArray();
        if (effectiveBuckets.Length == 0)
        {
            throw new ThreadMigrationException($"Target account '{targetAccountId}' quota is unknown. Refresh quota and retry.");
        }

        foreach (var bucket in effectiveBuckets)
        {
            var reserve = bucket.WindowDuration is { } duration && duration > TimeSpan.FromDays(1)
                ? preferences?.LongReservePercent ?? settings.LongReservePercent
                : preferences?.ShortReservePercent ?? settings.ShortReservePercent;
            if (bucket.RemainingPercent <= reserve)
            {
                throw new ThreadMigrationException(
                    $"Target account '{targetAccountId}' has insufficient quota: " +
                    $"{bucket.LimitId}/{bucket.Slot} remaining={bucket.RemainingPercent}% reserve={reserve}%.");
            }
        }
    }

    private void StartBackground(string jobId, CancellationToken callerToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token, callerToken);
        if (!_running.TryAdd(jobId, linked))
        {
            linked.Dispose();
            throw new ThreadMigrationException($"Migration '{jobId}' is already running.");
        }
        var task = Task.Run(async () =>
        {
            try { await ContinueAsync(jobId, linked.Token).ConfigureAwait(false); }
            catch (ThreadMigrationException) { }
            finally
            {
                if (_running.TryRemove(jobId, out var removed)) removed.Dispose();
                _backgroundTasks.TryRemove(jobId, out _);
            }
        }, CancellationToken.None);
        _backgroundTasks[jobId] = task;
    }

    private async Task ContinueAsync(string jobId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var stored = await RequireStoredJobAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            var state = ParseState(stored.State);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (state)
                {
                    case ThreadMigrationState.Pending:
                        await TransitionAsync(stored, ThreadMigrationState.Snapshotting,
                            message: "capturing source thread and workspace snapshot",
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        continue;

                    case ThreadMigrationState.Snapshotting:
                        await SnapshotAsync(stored, cancellationToken).ConfigureAwait(false);
                        continue;

                    case ThreadMigrationState.CreatingTarget:
                        await CreateTargetAsync(stored, cancellationToken).ConfigureAwait(false);
                        continue;

                    case ThreadMigrationState.Seeding:
                        await SeedTargetAsync(stored, cancellationToken).ConfigureAwait(false);
                        continue;

                    case ThreadMigrationState.Completed:
                    case ThreadMigrationState.Canceled:
                        return;

                    case ThreadMigrationState.Failed:
                        return;

                    default:
                        throw new ThreadMigrationException($"Unsupported migration state '{state}'.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var current = await RequireStoredJobAsync(jobId, CancellationToken.None).ConfigureAwait(false);
                if (ParseState(current.State) is not (ThreadMigrationState.Completed or ThreadMigrationState.Canceled))
                {
                    try
                    {
                        await TransitionAsync(current, ThreadMigrationState.Canceled,
                            message: "migration canceled",
                            markCompleted: true,
                            cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (StorageException) { }
                }
                throw new ThreadMigrationCanceledException(jobId);
            }
            catch (Exception ex)
            {
                var current = await RequireStoredJobAsync(jobId, CancellationToken.None).ConfigureAwait(false);
                if (ParseState(current.State) is not (ThreadMigrationState.Completed or ThreadMigrationState.Canceled or ThreadMigrationState.Failed))
                {
                    try
                    {
                        await TransitionAsync(current, ThreadMigrationState.Failed,
                            error: ex.Message,
                            message: $"failed in {current.State}: {ex.Message}",
                            cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (StorageException) { }
                }
                throw new ThreadMigrationException($"Thread migration '{jobId}' failed in state '{state}'.", ex);
            }
        }
    }

    private async Task SnapshotAsync(StoredThreadMigrationJob job, CancellationToken cancellationToken)
    {
        var sourceProfile = (await _repository.GetAccountAsync(job.SourceAccountId, cancellationToken).ConfigureAwait(false))?.Profile
            ?? throw new ThreadMigrationException($"Source account '{job.SourceAccountId}' no longer exists.");
        await using var lease = await _workerPool.AcquireAsync(sourceProfile, cancellationToken).ConfigureAwait(false);
        var thread = await lease.Worker.SendRetryableRequestAsync(
            "thread/read",
            new { threadId = job.SourceThreadId.Value, includeTurns = true },
            DateTimeOffset.UtcNow.AddSeconds(30),
            retryable: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSourceThreadCanMigrate(thread);
        var snapshot = await _snapshotBuilder.BuildAsync(
            job.SourceThreadId,
            job.SourceAccountId,
            job.TargetAccountId,
            thread,
            cancellationToken).ConfigureAwait(false);
        var snapshotJson = JsonSerializer.Serialize(snapshot, _json);
        var handoff = $"Codex Router migration job: {job.Id}\n\n{_snapshotBuilder.BuildHandoffText(snapshot)}";
        await _repository.TransitionThreadMigrationJobAsync(
            job.Id,
            job.State,
            StateText(ThreadMigrationState.CreatingTarget),
            snapshotJson: snapshotJson,
            handoffText: handoff,
            message: "snapshot captured",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureSourceThreadCanMigrate(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("thread", out var thread) ||
            thread.ValueKind != JsonValueKind.Object)
        {
            throw new ThreadMigrationException("thread/read response does not contain a thread object.");
        }

        var status = thread.TryGetProperty("status", out var statusElement) &&
                     statusElement.ValueKind == JsonValueKind.Object &&
                     statusElement.TryGetProperty("type", out var typeElement) &&
                     typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;
        if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
        {
            throw new ThreadMigrationException(
                "Source thread is active. Wait for the current turn to finish or stop it before migrating.");
        }
        if (status is null ||
            !new[] { "idle", "notLoaded", "systemError" }.Contains(status, StringComparer.OrdinalIgnoreCase))
        {
            throw new ThreadMigrationException(
                $"Source thread status '{status ?? "unknown"}' is not safe for migration.");
        }

        if (!thread.TryGetProperty("turns", out var turns) || turns.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (var turn in turns.EnumerateArray())
        {
            if (turn.ValueKind == JsonValueKind.Object &&
                turn.TryGetProperty("status", out var turnStatus) &&
                turnStatus.ValueKind == JsonValueKind.String &&
                string.Equals(turnStatus.GetString(), "inProgress", StringComparison.OrdinalIgnoreCase))
            {
                throw new ThreadMigrationException(
                    "Source thread still has an in-progress turn. Wait for it to finish or stop it before migrating.");
            }
        }
    }

    private async Task CreateTargetAsync(StoredThreadMigrationJob job, CancellationToken cancellationToken)
    {
        if (job.SnapshotJson is null || job.HandoffText is null)
        {
            throw new ThreadMigrationException("Migration snapshot is missing before target creation.");
        }
        var snapshot = JsonSerializer.Deserialize<ThreadMigrationSnapshot>(job.SnapshotJson, _json)
            ?? throw new ThreadMigrationException("Migration snapshot could not be deserialized.");
        var targetProfile = (await _repository.GetAccountAsync(job.TargetAccountId, cancellationToken).ConfigureAwait(false))?.Profile
            ?? throw new ThreadMigrationException($"Target account '{job.TargetAccountId}' no longer exists.");
        if (!targetProfile.Enabled) throw new ThreadMigrationException("Target account became disabled during migration.");

        await using var lease = await _workerPool.AcquireAsync(targetProfile, cancellationToken).ConfigureAwait(false);
        object parameters = snapshot.Cwd is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["cwd"] = snapshot.Cwd };
        var response = await lease.Worker.SendRequestAsync(
            "thread/start",
            parameters,
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        var targetThreadId = ExtractThreadId(response, "thread/start");

        try
        {
            await _repository.InsertThreadRouteAsync(new ThreadRoute(
                targetThreadId,
                job.TargetAccountId,
                lease.Worker.WorkerId,
                RouteReason.Recovery,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            await _repository.TransitionThreadMigrationJobAsync(
                job.Id,
                job.State,
                StateText(ThreadMigrationState.Seeding),
                targetThreadId: targetThreadId,
                message: $"target thread created: {targetThreadId.Value}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                await _repository.RecordOrphanThreadAsync(new OrphanThreadRecord(
                    targetThreadId,
                    job.TargetAccountId,
                    lease.Worker.WorkerId,
                    $"migration target persistence failed: {ex.Message}",
                    DateTimeOffset.UtcNow,
                    null), CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
            throw;
        }
    }

    private async Task SeedTargetAsync(StoredThreadMigrationJob job, CancellationToken cancellationToken)
    {
        if (job.TargetThreadId is null || string.IsNullOrWhiteSpace(job.HandoffText))
        {
            throw new ThreadMigrationException("Migration target thread or handoff text is missing before seeding.");
        }
        var targetProfile = (await _repository.GetAccountAsync(job.TargetAccountId, cancellationToken).ConfigureAwait(false))?.Profile
            ?? throw new ThreadMigrationException($"Target account '{job.TargetAccountId}' no longer exists.");
        await using var lease = await _workerPool.AcquireAsync(targetProfile, cancellationToken).ConfigureAwait(false);

        if (await TargetAlreadySeededAsync(lease.Worker, job, cancellationToken).ConfigureAwait(false))
        {
            await _repository.TransitionThreadMigrationJobAsync(
                job.Id,
                job.State,
                StateText(ThreadMigrationState.Completed),
                message: "handoff already present in target thread; retry converged without duplicate turn",
                changedAt: DateTimeOffset.UtcNow,
                markCompleted: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        _ = await lease.Worker.SendRequestAsync(
            "turn/start",
            new
            {
                threadId = job.TargetThreadId.Value.Value,
                input = new[] { new { type = "text", text = job.HandoffText } }
            },
            TimeSpan.FromMinutes(3),
            cancellationToken).ConfigureAwait(false);
        await _repository.TransitionThreadMigrationJobAsync(
            job.Id,
            job.State,
            StateText(ThreadMigrationState.Completed),
            message: "handoff seeded into target thread",
            changedAt: DateTimeOffset.UtcNow,
            markCompleted: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TargetAlreadySeededAsync(
        IAppServerWorker worker,
        StoredThreadMigrationJob job,
        CancellationToken cancellationToken)
    {
        if (job.TargetThreadId is null) return false;
        try
        {
            var result = await worker.SendRetryableRequestAsync(
                "thread/read",
                new { threadId = job.TargetThreadId.Value.Value, includeTurns = true },
                DateTimeOffset.UtcNow.AddSeconds(20),
                retryable: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.GetRawText().Contains($"Codex Router migration job: {job.Id}", StringComparison.Ordinal);
        }
        catch (AppServerRpcException ex) when (ex.Code == -32602 || ex.Code == -32601)
        {
            return false;
        }
    }

    private Task TransitionAsync(
        StoredThreadMigrationJob job,
        ThreadMigrationState next,
        string? error = null,
        string? message = null,
        bool markCompleted = false,
        CancellationToken cancellationToken = default) =>
        _repository.TransitionThreadMigrationJobAsync(
            job.Id,
            job.State,
            StateText(next),
            error: error,
            message: message,
            markCompleted: markCompleted,
            cancellationToken: cancellationToken);

    private async Task<StoredThreadMigrationJob> RequireStoredJobAsync(string jobId, CancellationToken cancellationToken) =>
        await _repository.GetThreadMigrationJobAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new ThreadMigrationException($"Migration job '{jobId}' does not exist.");

    private ThreadMigrationJob Map(StoredThreadMigrationJob job) => new(
        job.Id,
        job.SourceThreadId,
        job.SourceAccountId,
        job.TargetAccountId,
        job.TargetThreadId,
        ParseState(job.State),
        job.SnapshotJson is null ? null : JsonSerializer.Deserialize<ThreadMigrationSnapshot>(job.SnapshotJson, _json),
        job.HandoffText,
        job.Error,
        job.CreatedAt,
        job.UpdatedAt,
        job.CompletedAt);

    private static ThreadId ExtractThreadId(JsonElement response, string method)
    {
        if (response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("thread", out var thread) &&
            thread.ValueKind == JsonValueKind.Object &&
            thread.TryGetProperty("id", out var id) &&
            id.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(id.GetString()))
        {
            return new ThreadId(id.GetString()!);
        }
        throw new ThreadMigrationException($"{method} response does not contain thread.id.");
    }

    private static string StateText(ThreadMigrationState state) => state switch
    {
        ThreadMigrationState.Pending => "pending",
        ThreadMigrationState.Snapshotting => "snapshotting",
        ThreadMigrationState.CreatingTarget => "creating-target",
        ThreadMigrationState.Seeding => "seeding",
        ThreadMigrationState.Completed => "completed",
        ThreadMigrationState.Failed => "failed",
        ThreadMigrationState.Canceled => "canceled",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static ThreadMigrationState ParseState(string state) => state switch
    {
        "pending" => ThreadMigrationState.Pending,
        "snapshotting" => ThreadMigrationState.Snapshotting,
        "creating-target" => ThreadMigrationState.CreatingTarget,
        "seeding" => ThreadMigrationState.Seeding,
        "completed" => ThreadMigrationState.Completed,
        "failed" => ThreadMigrationState.Failed,
        "canceled" => ThreadMigrationState.Canceled,
        _ => throw new ThreadMigrationException($"Unknown persisted migration state '{state}'.")
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
