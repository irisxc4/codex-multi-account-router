using CodexRouter.Domain;
using CodexRouter.Routing;
using CodexRouter.Storage;
using CodexRouter.Workers;

namespace CodexRouter.Rpc;

public sealed class FrontAccountProjection
{
    private readonly RouterRepository _repository;
    private readonly object _gate = new();
    private AccountId? _current;

    public FrontAccountProjection(RouterRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public AccountId? Current
    {
        get { lock (_gate) return _current; }
    }

    public void Set(AccountId accountId)
    {
        lock (_gate)
        {
            _current = accountId;
        }
    }

    public async Task<AccountId> GetOrSelectDefaultAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_current is { } current)
            {
                return current;
            }
        }

        var settings = await _repository.GetRouterSettingsAsync(cancellationToken).ConfigureAwait(false);
        var accounts = await _repository.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        AccountId? selected = null;
        if (settings.PinnedAccountId is { } pinned && accounts.Any(account => account.Profile.Id == pinned))
        {
            selected = pinned;
        }
        selected ??= accounts.FirstOrDefault(account => account.Profile.Enabled)?.Profile.Id;
        selected ??= accounts.FirstOrDefault()?.Profile.Id;
        if (selected is null)
        {
            throw new InvalidOperationException("No account profile exists for front account projection.");
        }

        Set(selected.Value);
        return selected.Value;
    }
}

public sealed class ThreadOwnershipCollisionException : Exception
{
    public ThreadOwnershipCollisionException(ThreadId threadId, IReadOnlyList<AccountId> accounts)
        : base($"Thread '{threadId}' exists in multiple account profiles: {string.Join(", ", accounts)}")
    {
        ThreadId = threadId;
        Accounts = accounts;
    }

    public ThreadId ThreadId { get; }
    public IReadOnlyList<AccountId> Accounts { get; }
}

public sealed class RpcWorkerAccess
{
    private readonly RouterRepository _repository;
    private readonly WorkerPool _pool;
    private readonly RouterCoordinator _router;
    private readonly Action<IAppServerWorker> _registerWorker;

    public RpcWorkerAccess(
        RouterRepository repository,
        WorkerPool pool,
        RouterCoordinator router,
        Action<IAppServerWorker> registerWorker)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _registerWorker = registerWorker ?? throw new ArgumentNullException(nameof(registerWorker));
    }

    public async Task<WorkerLease> AcquireAccountAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        var stored = await _repository.GetAccountAsync(accountId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Account '{accountId}' does not exist.");
        var lease = await _pool.AcquireAsync(stored.Profile, cancellationToken).ConfigureAwait(false);
        _registerWorker(lease.Worker);
        return lease;
    }

    public async Task<ThreadRoute> ResolveOrDiscoverThreadAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default)
    {
        var existing = await _router.ResolveThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var accounts = await _repository.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        var matches = new List<(AccountId AccountId, WorkerId WorkerId)>();
        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var lease = await AcquireAccountAsync(account.Profile.Id, cancellationToken).ConfigureAwait(false);
                var response = await lease.Worker.SendRequestAsync(
                    "thread/read",
                    new { threadId = threadId.Value, includeTurns = false },
                    TimeSpan.FromSeconds(10),
                    cancellationToken).ConfigureAwait(false);
                if (response.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    response.TryGetProperty("thread", out var thread) &&
                    thread.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    thread.TryGetProperty("id", out var idElement) &&
                    string.Equals(idElement.GetString(), threadId.Value, StringComparison.Ordinal))
                {
                    matches.Add((account.Profile.Id, lease.Worker.WorkerId));
                }
            }
            catch (AppServerRpcException)
            {
                // A missing thread is reported as an RPC error by some Codex builds. Continue discovery.
            }
            catch (TimeoutException)
            {
                // Do not claim ownership from a worker that did not answer.
            }
        }

        if (matches.Count == 0)
        {
            throw new KeyNotFoundException($"Thread '{threadId}' was not found in any account profile.");
        }
        if (matches.Count > 1)
        {
            throw new ThreadOwnershipCollisionException(threadId, matches.Select(static match => match.AccountId).ToArray());
        }

        var match = matches[0];
        var route = new ThreadRoute(
            threadId,
            match.AccountId,
            match.WorkerId,
            RouteReason.Recovery,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        try
        {
            await _repository.InsertThreadRouteAsync(route, cancellationToken).ConfigureAwait(false);
            return route;
        }
        catch (StorageException)
        {
            return await _router.RequireThreadRouteAsync(threadId, cancellationToken).ConfigureAwait(false);
        }
    }
}
