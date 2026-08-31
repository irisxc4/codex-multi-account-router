using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using CodexRouter.Domain;
using CodexRouter.Protocol;
using CodexRouter.Storage;
using CodexRouter.Workers;

namespace CodexRouter.Accounts;

public sealed class AccountService : IAsyncDisposable
{
    private readonly RouterRepository _repository;
    private readonly WorkerPool _workerPool;
    private readonly ProfileMaterializer _profileMaterializer;
    private readonly CodexProtocolAdapter _adapter;
    private readonly QuotaSnapshotMerger _quotaMerger;
    private readonly AccountHealthEvaluator _healthEvaluator;
    private readonly IExternalUriLauncher _uriLauncher;
    private readonly AccountServiceOptions _options;
    private readonly Channel<WorkerNotification> _notificationQueue = Channel.CreateUnbounded<WorkerNotification>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly ConcurrentDictionary<string, IAppServerWorker> _observedWorkers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<QuotaSnapshot>> _quotaRefreshes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _quotaGates = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _notificationProcessor;
    private readonly Task? _quotaRefreshProcessor;
    private int _disposed;

    public AccountService(
        RouterRepository repository,
        WorkerPool workerPool,
        ProfileMaterializer profileMaterializer,
        CodexProtocolAdapter? adapter = null,
        QuotaSnapshotMerger? quotaMerger = null,
        AccountHealthEvaluator? healthEvaluator = null,
        IExternalUriLauncher? uriLauncher = null,
        AccountServiceOptions? options = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _workerPool = workerPool ?? throw new ArgumentNullException(nameof(workerPool));
        _profileMaterializer = profileMaterializer ?? throw new ArgumentNullException(nameof(profileMaterializer));
        _adapter = adapter ?? new CodexProtocolAdapter();
        _quotaMerger = quotaMerger ?? new QuotaSnapshotMerger();
        _healthEvaluator = healthEvaluator ?? new AccountHealthEvaluator();
        _uriLauncher = uriLauncher ?? new WindowsExternalUriLauncher();
        _options = options ?? new AccountServiceOptions();

        ValidateOptions(_options);
        _workerPool.WorkerReady += OnWorkerReady;
        _notificationProcessor = Task.Run(ProcessNotificationsAsync, CancellationToken.None);
        if (_options.EnableQuotaBackgroundRefresh)
        {
            _quotaRefreshProcessor = Task.Run(ProcessQuotaRefreshesAsync, CancellationToken.None);
        }
    }

    public Task<IReadOnlyList<StoredAccount>> ListAccountsAsync(CancellationToken cancellationToken = default) =>
        _repository.ListAccountsAsync(cancellationToken);

    public async Task<int> CleanupPendingOnboardingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var pending = (await _repository.ListAllAccountsAsync(cancellationToken).ConfigureAwait(false))
            .Where(static account => account.Lifecycle == AccountLifecycle.Pending)
            .ToArray();
        foreach (var account in pending)
        {
            await DeleteAccountAsync(account.Profile.Id, force: true, cancellationToken).ConfigureAwait(false);
        }
        return pending.Length;
    }

    public async Task<AccountProfile> CreateAccountProfileAsync(
        string alias,
        SharedTemplate template,
        AccountId? accountId = null,
        bool enabled = true,
        int priority = 0,
        AccountLifecycle lifecycle = AccountLifecycle.Active,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(alias))
        {
            throw new ArgumentException("Account alias cannot be empty.", nameof(alias));
        }

        var id = accountId ?? new AccountId($"acct-{Guid.NewGuid():N}");
        var materialized = await _profileMaterializer.MaterializeAsync(id, template, cancellationToken).ConfigureAwait(false);
        var profile = new AccountProfile(id, alias.Trim(), materialized.CodexHome, Enabled: enabled, Priority: priority);
        try
        {
            await _repository.CreateAccountAsync(profile, lifecycle: lifecycle, cancellationToken: cancellationToken).ConfigureAwait(false);
            return profile;
        }
        catch
        {
            try { await _profileMaterializer.DeleteProfileAsync(id, CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    public async Task<AccountOnboardingResult> OnboardChatGptAsync(
        string alias,
        SharedTemplate template,
        bool openBrowser = true,
        CancellationToken cancellationToken = default)
    {
        // A ChatGPT account is not routable until OAuth has completed successfully.
        // Keeping the profile disabled also prevents a pending onboarding worker from
        // leaking into routing decisions if the UI disappears or the browser flow fails.
        var profile = await CreateAccountProfileAsync(
            alias,
            template,
            enabled: false,
            lifecycle: AccountLifecycle.Pending,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            var login = await BeginChatGptLoginAsync(profile.Id, openBrowser, cancellationToken).ConfigureAwait(false);
            return new AccountOnboardingResult(profile, login);
        }
        catch
        {
            try { await DeleteAccountAsync(profile.Id, force: true, cancellationToken: CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    public async Task<ChatGptLoginSession> BeginChatGptLoginAsync(
        AccountId accountId,
        bool openBrowser = true,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var profile = await RequireProfileAsync(accountId, cancellationToken).ConfigureAwait(false);
        var lease = await _workerPool.AcquireAsync(profile, cancellationToken).ConfigureAwait(false);
        AttachWorker(lease.Worker);
        try
        {
            var response = await lease.Worker.SendRequestAsync(
                "account/login/start",
                new
                {
                    type = "chatgpt",
                    useHostedLoginSuccessPage = true,
                    appBrand = "codex"
                },
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);

            if (response.ValueKind != JsonValueKind.Object ||
                !response.TryGetProperty("type", out var typeElement) ||
                !string.Equals(typeElement.GetString(), "chatgpt", StringComparison.Ordinal) ||
                !response.TryGetProperty("loginId", out var loginIdElement) ||
                loginIdElement.ValueKind != JsonValueKind.String ||
                !response.TryGetProperty("authUrl", out var authUrlElement) ||
                authUrlElement.ValueKind != JsonValueKind.String)
            {
                throw new AccountServiceException("Codex returned an invalid ChatGPT login-start response.");
            }

            var loginId = loginIdElement.GetString();
            var authUrlText = authUrlElement.GetString();
            if (string.IsNullOrWhiteSpace(loginId) ||
                !Uri.TryCreate(authUrlText, UriKind.Absolute, out var authUrl) ||
                authUrl.Scheme is not ("http" or "https"))
            {
                throw new AccountServiceException("Codex returned an unusable ChatGPT authentication URL or login id.");
            }

            var session = new ChatGptLoginSession(lease, loginId, authUrl, DateTimeOffset.UtcNow);
            if (openBrowser)
            {
                try
                {
                    await _uriLauncher.OpenAsync(authUrl, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            return session;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<AccountProfile> CompleteChatGptLoginAsync(
        ChatGptLoginSession session,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var completion = await session.WaitForCompletionAsync(timeout ?? _options.EffectiveLoginTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!completion.Success)
        {
            await AppendHealthAsync(_healthEvaluator.AuthRequired(
                session.AccountId,
                completion.Error ?? "ChatGPT login failed",
                completion.CompletedAt), CancellationToken.None).ConfigureAwait(false);
            throw new AccountServiceException(completion.Error ?? "ChatGPT login failed.");
        }

        return await CompletePendingExternalLoginAsync(session.AccountId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccountProfile> CompletePendingExternalLoginAsync(
        AccountId accountId,
        CancellationToken cancellationToken = default)
    {
        var stored = await _repository.GetAccountAsync(accountId, cancellationToken).ConfigureAwait(false)
            ?? throw new AccountNotFoundException(accountId);
        if (stored.Lifecycle != AccountLifecycle.Pending)
        {
            return await RefreshAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        }

        var validationProfile = stored.Profile with { Enabled = true };
        await _repository.UpdateAccountAsync(validationProfile, cancellationToken).ConfigureAwait(false);
        try
        {
            var (refreshed, observation) = await RefreshAccountWithObservationAsync(accountId, cancellationToken).ConfigureAwait(false);
            if (observation.AuthKind != AccountAuthKind.ChatGpt)
            {
                throw new AccountServiceException("Official Codex login did not produce a ChatGPT account session.");
            }
            var activated = refreshed with { Enabled = true };
            await _repository.UpdateAccountAsync(activated, cancellationToken).ConfigureAwait(false);
            if (!await _repository.SetAccountLifecycleAsync(accountId, AccountLifecycle.Active, cancellationToken).ConfigureAwait(false))
            {
                throw new AccountNotFoundException(accountId);
            }
            return activated;
        }
        catch
        {
            try { await _repository.UpdateAccountAsync(stored.Profile with { Enabled = false }, CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    public async Task<AccountProfile> RefreshAccountAsync(
        AccountId accountId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var profile = await RequireProfileAsync(accountId, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await _workerPool.AcquireAsync(profile, cancellationToken).ConfigureAwait(false);
            AttachWorker(lease.Worker);
            var response = await lease.Worker.SendRequestAsync(
                "account/read",
                new { refreshToken = false },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            var mapped = _adapter.MapAccountRead(accountId, response.GetRawText());
            if (!mapped.Succeeded || mapped.Value is null)
            {
                throw new AccountServiceException(string.Join("; ", mapped.Errors));
            }

            var observation = mapped.Value;
            var updated = profile with
            {
                Email = observation.Email ?? profile.Email,
                PlanType = observation.PlanType ?? profile.PlanType
            };
            await _repository.UpdateAccountAsync(updated, cancellationToken).ConfigureAwait(false);
            await _repository.SetAccountLastSeenAsync(accountId, observation.ObservedAt, cancellationToken).ConfigureAwait(false);
            var quota = await _repository.GetLatestQuotaSnapshotAsync(accountId, cancellationToken).ConfigureAwait(false);
            await AppendHealthAsync(_healthEvaluator.Evaluate(
                updated, observation, quota, observation.ObservedAt,
                _options.ShortReservePercent, _options.LongReservePercent), cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch (Exception ex) when (ex is WorkerExitedException or AppServerRpcException or TimeoutException)
        {
            await AppendHealthAsync(_healthEvaluator.Degraded(accountId, ex.Message), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<(AccountProfile Profile, AccountObservation Observation)> RefreshAccountWithObservationAsync(
        AccountId accountId,
        CancellationToken cancellationToken)
    {
        var refreshed = await RefreshAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        await using var lease = await _workerPool.AcquireAsync(refreshed, cancellationToken).ConfigureAwait(false);
        AttachWorker(lease.Worker);
        var response = await lease.Worker.SendRequestAsync(
            "account/read",
            new { refreshToken = false },
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        var mapped = _adapter.MapAccountRead(accountId, response.GetRawText());
        if (!mapped.Succeeded || mapped.Value is null)
        {
            throw new AccountServiceException(string.Join("; ", mapped.Errors));
        }
        return (refreshed, mapped.Value);
    }

    public async Task<QuotaSnapshot> RefreshQuotaAsync(
        AccountId accountId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var refresh = _quotaRefreshes.GetOrAdd(
            accountId.Value,
            _ => RefreshQuotaCoreAsync(accountId, _lifetimeCts.Token));
        try
        {
            // A caller cancelling its wait must not cancel the shared network
            // refresh. The next caller can still consume that result.
            return await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (refresh.IsCompleted)
            {
                _quotaRefreshes.TryRemove(new KeyValuePair<string, Task<QuotaSnapshot>>(accountId.Value, refresh));
            }
        }
    }

    private async Task<QuotaSnapshot> RefreshQuotaCoreAsync(
        AccountId accountId,
        CancellationToken cancellationToken)
    {
        var profile = await RequireProfileAsync(accountId, cancellationToken).ConfigureAwait(false);
        var gate = _quotaGates.GetOrAdd(accountId.Value, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var baseline = await _repository.GetLatestQuotaSnapshotAsync(accountId, cancellationToken).ConfigureAwait(false);
            try
            {
                await using var lease = await _workerPool.AcquireAsync(profile, cancellationToken).ConfigureAwait(false);
                AttachWorker(lease.Worker);
                var response = await lease.Worker.SendRequestAsync(
                    "account/rateLimits/read",
                    null,
                    TimeSpan.FromSeconds(15),
                    cancellationToken).ConfigureAwait(false);
                var mapped = _adapter.MapRateLimitsRead(accountId, response.GetRawText());
                if (!mapped.Succeeded || mapped.Value is null)
                {
                    throw new AccountServiceException(string.Join("; ", mapped.Errors));
                }

                var refreshed = mapped.Value;
                // Empty data after a usable full read is treated as an
                // incomplete response. It must not erase a last-known-good
                // snapshot and later appear as optimistic 100% headroom.
                if (refreshed.Buckets.Count == 0 && baseline is { Buckets.Count: > 0 })
                {
                    return baseline;
                }

                await _repository.AppendQuotaSnapshotAsync(refreshed, cancellationToken).ConfigureAwait(false);
                try
                {
                    var health = _healthEvaluator.Evaluate(
                        profile, null, refreshed, refreshed.FetchedAt,
                        _options.ShortReservePercent, _options.LongReservePercent);
                    await AppendHealthAsync(health, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Quota persistence is authoritative; a health-audit
                    // write failure must not report a successful read as a
                    // failed refresh or erase the new snapshot.
                }
                return refreshed;
            }
            catch (Exception ex) when (ex is WorkerExitedException or AppServerRpcException or TimeoutException or AccountServiceException)
            {
                // Preserve the last-known-good snapshot on transport, worker,
                // or mapping failure. Health records the failure, but no quota
                // replacement is written.
                try
                {
                    await AppendHealthAsync(_healthEvaluator.Degraded(accountId, ex.Message), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch { }
                if (baseline is not null)
                {
                    return baseline;
                }
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<QuotaState> GetQuotaStateAsync(
        AccountId accountId,
        TimeSpan? staleAfter = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _repository.GetLatestQuotaSnapshotAsync(accountId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        if (snapshot is null)
        {
            return new QuotaState(null, true, null, now);
        }
        var age = now - snapshot.FetchedAt;
        return new QuotaState(snapshot, age > (staleAfter ?? _options.EffectiveQuotaStaleAfter), age, now);
    }

    /// <summary>
    /// Refreshes only active accounts whose last successful snapshot is stale.
    /// Each account has one coalesced in-flight read; an individual failure is
    /// deliberately isolated so other accounts still get refreshed.
    /// </summary>
    public async Task RefreshStaleQuotasAsync(
        TimeSpan? staleAfter = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var threshold = staleAfter ?? _options.EffectiveQuotaStaleAfter;
        if (threshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(staleAfter));
        }

        var accounts = await _repository.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var tasks = accounts
            .Where(static stored => stored.Profile.Enabled)
            .Select(async stored =>
            {
                QuotaSnapshot? latest;
                try
                {
                    latest = await _repository.GetLatestQuotaSnapshotAsync(stored.Profile.Id, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    return;
                }
                if (latest is not null && now - latest.FetchedAt <= threshold)
                {
                    return;
                }

                try
                {
                    _ = await RefreshQuotaAsync(stored.Profile.Id, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Last-known-good data remains in storage; the routing
                    // engine will reject it once stale instead of guessing.
                }
            })
            .ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task<UsageReadResult> RefreshUsageAsync(
        AccountId accountId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var profile = await RequireProfileAsync(accountId, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await _workerPool.AcquireAsync(profile, cancellationToken).ConfigureAwait(false);
            AttachWorker(lease.Worker);
            var response = await lease.Worker.SendRequestAsync(
                "account/usage/read",
                null,
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            var mapped = _adapter.MapUsageRead(accountId, response.GetRawText());
            if (!mapped.Succeeded || mapped.Value is null)
            {
                return new UsageReadResult(UsageAvailability.Failed, null, string.Join("; ", mapped.Errors));
            }
            await _repository.AppendUsageSnapshotAsync(mapped.Value, cancellationToken).ConfigureAwait(false);
            return new UsageReadResult(UsageAvailability.Available, mapped.Value);
        }
        catch (AppServerRpcException ex) when (ex.Code == -32601)
        {
            return new UsageReadResult(UsageAvailability.Unsupported, null, ex.Message);
        }
        catch (Exception ex) when (ex is WorkerExitedException or AppServerRpcException or TimeoutException)
        {
            return new UsageReadResult(UsageAvailability.Failed, null, ex.Message);
        }
    }

    public async Task<AccountProfile> RenameAsync(
        AccountId accountId,
        string alias,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            throw new ArgumentException("Account alias cannot be empty.", nameof(alias));
        }
        var profile = await RequireProfileAsync(accountId, cancellationToken).ConfigureAwait(false);
        var updated = profile with { Alias = alias.Trim() };
        await _repository.UpdateAccountAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<AccountProfile> SetEnabledAsync(
        AccountId accountId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var profile = await RequireProfileAsync(accountId, cancellationToken).ConfigureAwait(false);
        var updated = profile with { Enabled = enabled };
        await _repository.UpdateAccountAsync(updated, cancellationToken).ConfigureAwait(false);
        if (!enabled)
        {
            await AppendHealthAsync(new AccountHealth(accountId, AccountHealthState.Disabled, DateTimeOffset.UtcNow, "account disabled"), cancellationToken)
                .ConfigureAwait(false);
        }
        return updated;
    }

    public async Task LogoutAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        var profile = await RequireProfileAsync(accountId, cancellationToken).ConfigureAwait(false);
        await using var lease = await _workerPool.AcquireAsync(profile, cancellationToken).ConfigureAwait(false);
        AttachWorker(lease.Worker);
        _ = await lease.Worker.SendRequestAsync("account/logout", null, TimeSpan.FromSeconds(15), cancellationToken)
            .ConfigureAwait(false);
        await AppendHealthAsync(_healthEvaluator.AuthRequired(accountId, "logged out"), cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAccountAsync(
        AccountId accountId,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var stored = await _repository.GetAccountAsync(accountId, cancellationToken).ConfigureAwait(false)
            ?? throw new AccountNotFoundException(accountId);
        var profile = stored.Profile;
        var routes = await _repository.ListThreadRoutesForAccountAsync(accountId, 1, cancellationToken).ConfigureAwait(false);
        if (routes.Count > 0)
        {
            throw new AccountDeleteBlockedException(accountId, routes.Count);
        }

        // A pending onboarding profile has never been admitted as a routable account.
        // Starting an app-server solely to call account/logout here is both unnecessary
        // and harmful on Windows: the worker opens SQLite/plugin files inside CODEX_HOME,
        // which can prevent the profile directory from being staged for deletion.
        // If a failed verification already created a worker, EvictAsync below still
        // disposes that existing worker before the filesystem is touched.
        if (stored.Lifecycle == AccountLifecycle.Active)
        {
            Exception? logoutFailure = null;
            try
            {
                await LogoutAsync(accountId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WorkerExitedException or AppServerRpcException or TimeoutException or AccountServiceException)
            {
                logoutFailure = ex;
            }
            if (logoutFailure is not null && !force)
            {
                throw new AccountServiceException(
                    "Account logout failed. Refusing profile deletion so a credential-store entry is not orphaned. Use force only after deciding that is acceptable.",
                    logoutFailure);
            }
        }

        if (!await _workerPool.EvictAsync(accountId, cancellationToken).ConfigureAwait(false))
        {
            throw new AccountServiceException("Account profile is still leased by an active operation and cannot be deleted safely.");
        }

        await using var staged = await _profileMaterializer.StageProfileDeletionAsync(accountId, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await _repository.DeleteAccountAsync(accountId, cancellationToken).ConfigureAwait(false))
            {
                throw new AccountNotFoundException(accountId);
            }
            await staged.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { await staged.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _workerPool.WorkerReady -= OnWorkerReady;
        foreach (var worker in _observedWorkers.Values)
        {
            DetachWorker(worker);
        }
        _observedWorkers.Clear();
        _notificationQueue.Writer.TryComplete();
        _lifetimeCts.Cancel();
        try { await _notificationProcessor.ConfigureAwait(false); } catch (OperationCanceledException) { }
        if (_quotaRefreshProcessor is not null)
        {
            try { await _quotaRefreshProcessor.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        var inFlightQuotaRefreshes = _quotaRefreshes.Values.ToArray();
        if (inFlightQuotaRefreshes.Length > 0)
        {
            try { await Task.WhenAll(inFlightQuotaRefreshes).ConfigureAwait(false); } catch { }
        }
        foreach (var gate in _quotaGates.Values)
        {
            gate.Dispose();
        }
        _quotaGates.Clear();
        _lifetimeCts.Dispose();
    }

    private void OnWorkerReady(object? sender, WorkerReadyEvent ready) => AttachWorker(ready.Worker);

    private void AttachWorker(IAppServerWorker worker)
    {
        if (!_observedWorkers.TryAdd(worker.WorkerId.Value, worker))
        {
            return;
        }
        worker.NotificationReceived += OnWorkerNotification;
        worker.StateChanged += OnWorkerStateChanged;
    }

    private void DetachWorker(IAppServerWorker worker)
    {
        worker.NotificationReceived -= OnWorkerNotification;
        worker.StateChanged -= OnWorkerStateChanged;
    }

    private void OnWorkerStateChanged(object? sender, WorkerStateChange change)
    {
        if (change.Current is not (WorkerState.Stopped or WorkerState.Failed or WorkerState.Crashed or WorkerState.Quarantined) ||
            !_observedWorkers.TryRemove(change.WorkerId.Value, out var worker))
        {
            return;
        }
        DetachWorker(worker);
    }

    private void OnWorkerNotification(object? sender, WorkerNotification notification)
    {
        _notificationQueue.Writer.TryWrite(notification);
    }

    private async Task ProcessNotificationsAsync()
    {
        try
        {
            await foreach (var notification in _notificationQueue.Reader.ReadAllAsync(_lifetimeCts.Token).ConfigureAwait(false))
            {
                try
                {
                    if (notification.Method == "account/rateLimits/updated")
                    {
                        await ProcessSparseQuotaUpdateAsync(notification, _lifetimeCts.Token).ConfigureAwait(false);
                    }
                    else if (notification.Method == "account/updated")
                    {
                        await RefreshAccountAsync(notification.AccountId, _lifetimeCts.Token).ConfigureAwait(false);
                    }
                    else if (notification.Method == "account/login/completed" &&
                             notification.Parameters.ValueKind == JsonValueKind.Object &&
                             notification.Parameters.TryGetProperty("success", out var success) &&
                             success.ValueKind == JsonValueKind.True)
                    {
                        await RefreshAccountAsync(notification.AccountId, _lifetimeCts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    try
                    {
                        await AppendHealthAsync(_healthEvaluator.Degraded(notification.AccountId,
                            $"notification processing failed: {ex.Message}"), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Normal disposal.
        }
    }

    private async Task ProcessQuotaRefreshesAsync()
    {
        try
        {
            while (!_lifetimeCts.IsCancellationRequested)
            {
                try
                {
                    await RefreshStaleQuotasAsync(_options.EffectiveQuotaStaleAfter, _lifetimeCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // A transient repository/worker failure should defer this
                    // pass, not permanently disable automatic synchronization.
                }
                await Task.Delay(_options.EffectiveQuotaRefreshInterval, _lifetimeCts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Normal disposal.
        }
        catch
        {
            // A scheduler fault must not take down account management. The
            // explicit refresh path and notification path remain available.
        }
    }

    private async Task ProcessSparseQuotaUpdateAsync(WorkerNotification notification, CancellationToken cancellationToken)
    {
        var mapped = _adapter.MapRateLimitsUpdated(
            notification.AccountId,
            notification.Parameters.GetRawText(),
            notification.ReceivedAt);
        if (!mapped.Succeeded || mapped.Value is null)
        {
            _ = await RefreshQuotaAsync(notification.AccountId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var gate = _quotaGates.GetOrAdd(notification.AccountId.Value, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        QuotaSnapshot? baseline;
        try
        {
            baseline = await _repository.GetLatestQuotaSnapshotAsync(notification.AccountId, cancellationToken).ConfigureAwait(false);
            if (baseline is not null)
            {
                var merged = _quotaMerger.Merge(baseline, mapped.Value);
                await _repository.AppendQuotaSnapshotAsync(merged, cancellationToken).ConfigureAwait(false);
                var profile = await RequireProfileAsync(notification.AccountId, cancellationToken).ConfigureAwait(false);
                await AppendHealthAsync(_healthEvaluator.Evaluate(
                    profile, null, merged, merged.FetchedAt,
                    _options.ShortReservePercent, _options.LongReservePercent), cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        finally
        {
            gate.Release();
        }

        // Without a baseline, a sparse update cannot reconstruct omitted
        // windows safely. Fetch a full response outside the gate to avoid a
        // recursive lock and establish the next last-known-good baseline.
        if (baseline is null)
        {
            _ = await RefreshQuotaAsync(notification.AccountId, cancellationToken).ConfigureAwait(false);
            return;
        }
    }

    private async Task<AccountProfile> RequireProfileAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        var stored = await _repository.GetAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        return stored?.Profile ?? throw new AccountNotFoundException(accountId);
    }

    private Task AppendHealthAsync(AccountHealth health, CancellationToken cancellationToken) =>
        _repository.AppendHealthEventAsync(health, cancellationToken);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static void ValidateOptions(AccountServiceOptions options)
    {
        if (options.EffectiveLoginTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.LoginTimeout));
        if (options.EffectiveQuotaStaleAfter <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.QuotaStaleAfter));
        if (options.EffectiveQuotaRefreshInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.QuotaRefreshInterval));
        if (options.ShortReservePercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(options.ShortReservePercent));
        if (options.LongReservePercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(options.LongReservePercent));
    }
}
