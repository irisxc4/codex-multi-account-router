using System.Collections.Concurrent;
using CodexRouter.Domain;
using CodexRouter.Protocol;
using CodexRouter.Routing;
using CodexRouter.Storage;
using CodexRouter.Workers;

namespace CodexRouter.Rpc;

/// <summary>
/// Transport-side quota prefetch used by the app-server path. A full
/// account/rateLimits/read is only issued for stale accounts; concurrent route
/// requests share one in-flight read per account. Failed reads leave the
/// last-known-good snapshot untouched.
/// </summary>
public sealed class QuotaRefreshProvider : IQuotaFreshnessProvider, IAsyncDisposable
{
    private readonly RouterRepository _repository;
    private readonly WorkerPool _workerPool;
    private readonly CodexProtocolAdapter _adapter;
    private readonly ConcurrentDictionary<string, Task<QuotaSnapshot?>> _inFlight = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _disposeCts = new();
    private int _disposed;

    public QuotaRefreshProvider(
        RouterRepository repository,
        WorkerPool workerPool,
        CodexProtocolAdapter? adapter = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _workerPool = workerPool ?? throw new ArgumentNullException(nameof(workerPool));
        _adapter = adapter ?? new CodexProtocolAdapter();
    }

    public async Task RefreshStaleAsync(
        TimeSpan staleAfter,
        int shortReservePercent = 15,
        int longReservePercent = 8,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (staleAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(staleAfter));
        }
        if (shortReservePercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(shortReservePercent));
        }
        if (longReservePercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(longReservePercent));
        }

        var accounts = await _repository.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        var tasks = accounts
            .Where(static account => account.Profile.Enabled)
            .Select(account => RefreshIfStaleAsync(
                account.Profile,
                staleAfter,
                shortReservePercent,
                longReservePercent,
                cancellationToken))
            .ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        // A single account being logged out/quarantined must not prevent other
        // accounts from receiving a fresh quota. The account-level operation
        // records no replacement snapshot on failure, so routing will fail
        // closed on its previous stale value.
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    private async Task RefreshIfStaleAsync(
        AccountProfile profile,
        TimeSpan staleAfter,
        int shortReservePercent,
        int longReservePercent,
        CancellationToken cancellationToken)
    {
        QuotaSnapshot? latest;
        try
        {
            latest = await _repository.GetLatestQuotaSnapshotAsync(profile.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return;
        }
        if (latest is not null && DateTimeOffset.UtcNow - latest.FetchedAt <= staleAfter)
        {
            return;
        }

        var refresh = _inFlight.GetOrAdd(
            profile.Id.Value,
            _ => RefreshCoreAsync(profile, staleAfter, shortReservePercent, longReservePercent, _disposeCts.Token));
        try
        {
            await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The provider is shutting down; leave the existing snapshot alone.
        }
        catch
        {
            // RefreshCoreAsync is best effort. This guard also protects the
            // route request from an unexpected worker/repository failure.
        }
        finally
        {
            if (refresh.IsCompleted)
            {
                _inFlight.TryRemove(new KeyValuePair<string, Task<QuotaSnapshot?>>(profile.Id.Value, refresh));
            }
        }
    }

    private async Task<QuotaSnapshot?> RefreshCoreAsync(
        AccountProfile profile,
        TimeSpan staleAfter,
        int shortReservePercent,
        int longReservePercent,
        CancellationToken cancellationToken)
    {
        var baseline = await _repository.GetLatestQuotaSnapshotAsync(profile.Id, cancellationToken).ConfigureAwait(false);
        if (baseline is not null && DateTimeOffset.UtcNow - baseline.FetchedAt <= staleAfter)
        {
            return baseline;
        }
        try
        {
            await using var lease = await _workerPool.AcquireAsync(profile, cancellationToken).ConfigureAwait(false);
            var response = await lease.Worker.SendRequestAsync(
                "account/rateLimits/read",
                null,
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            var mapped = _adapter.MapRateLimitsRead(profile.Id, response.GetRawText());
            if (!mapped.Succeeded || mapped.Value is null)
            {
                return baseline;
            }

            var refreshed = mapped.Value;
            // An empty read after a known-good full read is treated as an
            // incomplete response, not as a real 100% quota. A genuinely
            // empty account remains stored as empty and is rejected by the
            // routing engine until a usable bucket arrives.
            if (refreshed.Buckets.Count == 0 && baseline is { Buckets.Count: > 0 })
            {
                return baseline;
            }

            await _repository.AppendQuotaSnapshotAsync(refreshed, cancellationToken).ConfigureAwait(false);
            try
            {
                await _repository.AppendHealthEventAsync(
                    EvaluateHealth(profile, refreshed, refreshed.FetchedAt, shortReservePercent, longReservePercent),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Quota persistence is the source of truth for routing. A
                // health audit failure must not make a successful refresh look
                // like a transport failure or roll back the new snapshot.
            }
            return refreshed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return baseline;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _disposeCts.Cancel();
            var inFlight = _inFlight.Values.ToArray();
            if (inFlight.Length > 0)
            {
                try { await Task.WhenAll(inFlight).ConfigureAwait(false); } catch { }
            }
            _disposeCts.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static AccountHealth EvaluateHealth(
        AccountProfile profile,
        QuotaSnapshot quota,
        DateTimeOffset now,
        int shortReservePercent,
        int longReservePercent)
    {
        if (!profile.Enabled)
        {
            return new AccountHealth(profile.Id, AccountHealthState.Disabled, now, "account disabled");
        }
        if (quota.Buckets.Count == 0)
        {
            return new AccountHealth(profile.Id, AccountHealthState.Unknown, now, "quota has no usable limit bucket");
        }
        if (quota.IsRateLimited)
        {
            var reset = quota.Buckets
                .Select(static bucket => bucket.ResetsAt)
                .Where(value => value is not null && value > now)
                .Select(static value => value!.Value)
                .DefaultIfEmpty()
                .Min();
            return new AccountHealth(
                profile.Id,
                AccountHealthState.Cooldown,
                now,
                quota.RateLimitReachedType ?? "rate limit reached",
                reset == default ? null : reset);
        }

        var general = quota.Buckets
            .Where(static bucket => bucket.LimitId.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
                                    bucket.LimitId.Equals("default", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var effective = general.Length > 0 ? general : quota.Buckets;
        foreach (var bucket in effective)
        {
            var reserve = bucket.WindowDuration is { } duration && duration > TimeSpan.FromDays(1)
                ? longReservePercent
                : shortReservePercent;
            if (bucket.RemainingPercent <= reserve)
            {
                return new AccountHealth(
                    profile.Id,
                    AccountHealthState.Draining,
                    now,
                    $"quota reserve reached: {bucket.LimitId}/{bucket.Slot} remaining={bucket.RemainingPercent}% reserve={reserve}%");
            }
        }
        return new AccountHealth(profile.Id, AccountHealthState.Healthy, now);
    }
}
