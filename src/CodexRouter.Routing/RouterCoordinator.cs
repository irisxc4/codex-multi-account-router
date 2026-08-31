using System.Text.Json;
using CodexRouter.Domain;
using CodexRouter.Storage;
using CodexRouter.Workers;

namespace CodexRouter.Routing;

public sealed class RouterCoordinator
{
    private readonly RouterRepository _repository;
    private readonly WorkerPool _workerPool;
    private readonly RoutingEngine _engine;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _temporaryPinGate = new();
    private IQuotaFreshnessProvider? _quotaFreshnessProvider;
    private TemporaryPin? _temporaryPin;

    public RouterCoordinator(
        RouterRepository repository,
        WorkerPool workerPool,
        RoutingEngine? engine = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _workerPool = workerPool ?? throw new ArgumentNullException(nameof(workerPool));
        _engine = engine ?? new RoutingEngine();
    }

    /// <summary>
    /// Installs the transport-specific stale-on-demand quota refresher. Keeping
    /// this as a small callback avoids making the routing assembly depend on a
    /// concrete worker/protocol implementation while still guaranteeing that
    /// the normal RPC path refreshes before evaluating candidates.
    /// </summary>
    public void SetQuotaFreshnessProvider(IQuotaFreshnessProvider? provider) =>
        Volatile.Write(ref _quotaFreshnessProvider, provider);

    public TemporaryPin? GetTemporaryPin(DateTimeOffset? now = null)
    {
        lock (_temporaryPinGate)
        {
            if (_temporaryPin is { } pin && pin.ExpiresAt <= (now ?? DateTimeOffset.UtcNow))
            {
                _temporaryPin = null;
            }
            return _temporaryPin;
        }
    }

    public void SetTemporaryPin(AccountId accountId, TimeSpan duration, DateTimeOffset? now = null)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
        lock (_temporaryPinGate)
        {
            _temporaryPin = new TemporaryPin(accountId, (now ?? DateTimeOffset.UtcNow) + duration);
        }
    }

    public void ClearTemporaryPin()
    {
        lock (_temporaryPinGate)
        {
            _temporaryPin = null;
        }
    }

    public Task<RouteSelection> SelectForNewThreadAsync(CancellationToken cancellationToken = default) =>
        SelectForNewThreadAsync(null, cancellationToken);

    public async Task<RouteSelection> SelectForNewThreadAsync(
        RouteRequestContext? requestContext,
        CancellationToken cancellationToken = default)
    {
        var settings = await _repository.GetRouterSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settings.Mode == RouterMode.Off)
        {
            throw new RoutingDisabledException();
        }

        var quotaFreshnessProvider = Volatile.Read(ref _quotaFreshnessProvider);
        if (quotaFreshnessProvider is not null)
        {
            try
            {
                await quotaFreshnessProvider.RefreshStaleAsync(
                        settings.QuotaStaleAfter,
                        settings.ShortReservePercent,
                        settings.LongReservePercent,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A provider may cancel its own best-effort refresh. Candidate
                // evaluation below still fails closed on stale/missing quota.
            }
            catch
            {
                // Preserve the last-known-good snapshot and let the engine
                // report quota:stale/quota:missing instead of routing on an
                // unverified 100% default.
            }
        }

        var now = DateTimeOffset.UtcNow;
        var temporary = GetTemporaryPin(now);
        var persistentPin = settings.Mode == RouterMode.Pinned ? settings.PinnedAccountId : null;
        var pin = temporary?.AccountId ?? persistentPin;
        var reason = pin is null ? RouteReason.AutoQuota : RouteReason.ManualPin;
        var candidates = await BuildCandidatesAsync(settings, now, cancellationToken).ConfigureAwait(false);
        var selection = _engine.Select(candidates, pin, reason, requestContext);

        await _repository.AppendRouteDecisionAuditAsync(new RouteDecisionAuditRecord(
            selection.DecisionId,
            null,
            selection.AccountId,
            selection.Reason.ToString(),
            JsonSerializer.Serialize(selection, _jsonOptions),
            selection.DecidedAt), cancellationToken).ConfigureAwait(false);
        return selection;
    }

    public async Task<ThreadRoute> BindNewThreadAsync(
        ThreadId threadId,
        WorkerId workerId,
        RouteSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.AccountId.Value.Length == 0)
        {
            throw new ArgumentException("Route selection has no winner.", nameof(selection));
        }

        var route = new ThreadRoute(
            threadId,
            selection.AccountId,
            workerId,
            selection.Reason,
            selection.DecidedAt,
            selection.DecidedAt);
        await _repository.CommitThreadRouteWithAuditAsync(route, selection.DecisionId, cancellationToken)
            .ConfigureAwait(false);
        return route;
    }

    public Task<ThreadRoute?> ResolveThreadAsync(ThreadId threadId, CancellationToken cancellationToken = default) =>
        _repository.GetThreadRouteAsync(threadId, cancellationToken);

    public async Task<ThreadRoute> RequireThreadRouteAsync(ThreadId threadId, CancellationToken cancellationToken = default)
    {
        return await ResolveThreadAsync(threadId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No sticky route is known for thread '{threadId}'.");
    }

    public Task<bool> TouchThreadAsync(
        ThreadId threadId,
        DateTimeOffset? usedAt = null,
        CancellationToken cancellationToken = default) =>
        _repository.TouchThreadRouteAsync(threadId, usedAt ?? DateTimeOffset.UtcNow, cancellationToken);

    public async Task<ThreadRoute> BindForkAsync(
        ThreadId sourceThreadId,
        ThreadId forkThreadId,
        WorkerId workerId,
        CancellationToken cancellationToken = default)
    {
        var source = await RequireThreadRouteAsync(sourceThreadId, cancellationToken).ConfigureAwait(false);
        var route = new ThreadRoute(
            forkThreadId,
            source.AccountId,
            workerId,
            RouteReason.Sticky,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        await _repository.InsertThreadRouteAsync(route, cancellationToken).ConfigureAwait(false);
        return route;
    }

    private async Task<IReadOnlyList<RouteCandidateSnapshot>> BuildCandidatesAsync(
        RouterSettings settings,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var accounts = await _repository.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        var compatibility = await _repository.GetLatestCompatibilityRunAsync(cancellationToken).ConfigureAwait(false);
        var compatibilityState = compatibility?.Report.State ?? CompatibilityState.Unknown;
        var workerSnapshots = await _workerPool.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var workerByAccount = workerSnapshots.ToDictionary(static snapshot => snapshot.AccountId.Value, StringComparer.Ordinal);

        var tasks = accounts.Select(async stored =>
        {
            var quotaTask = _repository.GetLatestQuotaSnapshotAsync(stored.Profile.Id, cancellationToken);
            var healthTask = _repository.GetHealthEventsAsync(stored.Profile.Id, 20, cancellationToken);
            var preferencesTask = _repository.GetAccountPreferencesAsync(stored.Profile.Id, cancellationToken);
            await Task.WhenAll(quotaTask, healthTask, preferencesTask).ConfigureAwait(false);

            var healthEvents = await healthTask.ConfigureAwait(false);
            var latestHealth = healthEvents.FirstOrDefault()?.Health;
            var recentFailures = healthEvents.Count(record => record.Health.State is
                AccountHealthState.Degraded or AccountHealthState.Cooldown or AccountHealthState.AuthRequired);
            var preferences = await preferencesTask.ConfigureAwait(false);
            var active = workerByAccount.TryGetValue(stored.Profile.Id.Value, out var worker) ? worker.ActiveLeases : 0;

            return new RouteCandidateSnapshot(
                stored.Profile,
                latestHealth,
                await quotaTask.ConfigureAwait(false),
                now,
                settings.QuotaStaleAfter,
                compatibilityState,
                active,
                recentFailures,
                preferences?.RouteWeight ?? 1.0);
        }).ToArray();

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
