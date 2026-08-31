using System.Diagnostics;
using System.IO;
using CodexRouter.Accounts;
using CodexRouter.Control;
using CodexRouter.Domain;
using CodexRouter.Host;
using CodexRouter.Protocol;
using CodexRouter.Storage;
using CodexRouter.Workers;

namespace CodexRouter.Overlay;

public sealed class OverlayController : IAsyncDisposable, IOfficialLoginClient
{
    private readonly RouterPaths _paths;
    private readonly RouterControlClient _control;
    private readonly CodexDesktopIntegrationManager _integration;
    private StorageDatabase? _database;
    private RouterRepository? _repository;
    private LocalAccountAdmin? _localAdmin;
    private bool _disposed;

    public OverlayController(RouterPaths? paths = null)
    {
        _paths = paths ?? RouterPaths.Default;
        _paths.EnsureCreated();
        _control = new RouterControlClient(_paths.Root);
        _integration = new CodexDesktopIntegrationManager(_paths);
    }

    public async Task<ControlSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (await _control.IsAvailableAsync(TimeSpan.FromMilliseconds(450), cancellationToken).ConfigureAwait(false))
        {
            return await _control.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        var repository = await GetRepositoryAsync(cancellationToken).ConfigureAwait(false);
        return await new ControlSnapshotReader(repository).ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetAutoAsync(CancellationToken cancellationToken = default)
    {
        if (await _control.IsAvailableAsync(TimeSpan.FromMilliseconds(450), cancellationToken).ConfigureAwait(false))
        {
            _ = await _control.SetAutoAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        var repository = await GetRepositoryAsync(cancellationToken).ConfigureAwait(false);
        var settings = await repository.GetRouterSettingsAsync(cancellationToken).ConfigureAwait(false);
        await repository.UpdateRouterSettingsAsync(settings with
        {
            Mode = RouterMode.Auto,
            PinnedAccountId = null,
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task PinAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var id = new AccountId(accountId);
        if (await _control.IsAvailableAsync(TimeSpan.FromMilliseconds(450), cancellationToken).ConfigureAwait(false))
        {
            _ = await _control.PinAsync(accountId, cancellationToken).ConfigureAwait(false);
            return;
        }
        var repository = await GetRepositoryAsync(cancellationToken).ConfigureAwait(false);
        if (await repository.GetAccountAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException($"Account '{accountId}' does not exist.");
        }
        var settings = await repository.GetRouterSettingsAsync(cancellationToken).ConfigureAwait(false);
        await repository.UpdateRouterSettingsAsync(settings with
        {
            Mode = RouterMode.Pinned,
            PinnedAccountId = id,
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetRouterOffAsync(CancellationToken cancellationToken = default)
    {
        if (await _control.IsAvailableAsync(TimeSpan.FromMilliseconds(450), cancellationToken).ConfigureAwait(false))
        {
            _ = await _control.SetOffAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        var repository = await GetRepositoryAsync(cancellationToken).ConfigureAwait(false);
        var settings = await repository.GetRouterSettingsAsync(cancellationToken).ConfigureAwait(false);
        await repository.UpdateRouterSettingsAsync(settings with
        {
            Mode = RouterMode.Off,
            PinnedAccountId = null,
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshQuotaAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (await _control.IsAvailableAsync(TimeSpan.FromMilliseconds(800), cancellationToken).ConfigureAwait(false))
        {
            _ = await _control.RefreshQuotaAsync(accountId, cancellationToken).ConfigureAwait(false);
            return;
        }
        var local = await GetLocalAdminAsync(cancellationToken).ConfigureAwait(false);
        _ = await local.Accounts.RefreshQuotaAsync(new AccountId(accountId), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControlLoginStart> StartOnboardAsync(
        string alias,
        string loginMethod,
        string? proxyUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(alias)) throw new ArgumentException("Account alias cannot be empty.", nameof(alias));
        if (string.IsNullOrWhiteSpace(loginMethod)) throw new ArgumentException("Login method cannot be empty.", nameof(loginMethod));
        if (await _control.IsAvailableAsync(TimeSpan.FromMilliseconds(800), cancellationToken).ConfigureAwait(false))
        {
            return await _control.StartOnboardAsync(alias.Trim(), loginMethod, proxyUrl, cancellationToken).ConfigureAwait(false);
        }

        await EnsureLocalAdminSafeAsync(cancellationToken).ConfigureAwait(false);
        var local = await GetLocalAdminAsync(cancellationToken).ConfigureAwait(false);
        return await local.StartOnboardAsync(alias.Trim(), loginMethod, proxyUrl, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChatGptSessionOnboardingResult> ImportChatGptSessionAsync(
        string sessionJson,
        string? proxyUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionJson)) throw new ArgumentException("ChatGPT session JSON cannot be empty.", nameof(sessionJson));
        var alias = $"ChatGPT-{DateTimeOffset.Now:HHmmss}";
        if (await _control.IsAvailableAsync(TimeSpan.FromMilliseconds(800), cancellationToken).ConfigureAwait(false))
        {
            return await _control.ImportChatGptSessionAsync(alias, sessionJson, proxyUrl, cancellationToken).ConfigureAwait(false);
        }

        await EnsureLocalAdminSafeAsync(cancellationToken).ConfigureAwait(false);
        var local = await GetLocalAdminAsync(cancellationToken).ConfigureAwait(false);
        return await local.ImportChatGptSessionAsync(alias, sessionJson, proxyUrl, cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameAccountAsync(
        string accountId,
        string alias,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(alias)) return;
        if (await _control.IsAvailableAsync(TimeSpan.FromMilliseconds(450), cancellationToken).ConfigureAwait(false))
        {
            _ = await _control.RenameAsync(accountId, alias.Trim(), cancellationToken).ConfigureAwait(false);
            return;
        }
        var local = await GetLocalAdminAsync(cancellationToken).ConfigureAwait(false);
        _ = await local.Accounts.RenameAsync(new AccountId(accountId), alias.Trim(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControlLoginStatus> GetLoginStatusAsync(
        string loginId,
        CancellationToken cancellationToken = default)
    {
        if (await _control.IsAvailableAsync(TimeSpan.FromMilliseconds(450), cancellationToken).ConfigureAwait(false))
        {
            return await _control.LoginStatusAsync(loginId, cancellationToken).ConfigureAwait(false);
        }
        var local = await GetLocalAdminAsync(cancellationToken).ConfigureAwait(false);
        return await local.GetLoginStatusAsync(loginId).ConfigureAwait(false);
    }

    public async Task<ControlLoginStatus> CancelLoginAsync(
        string loginId,
        CancellationToken cancellationToken = default)
    {
        if (await _control.IsAvailableAsync(TimeSpan.FromMilliseconds(450), cancellationToken).ConfigureAwait(false))
        {
            return await _control.CancelLoginAsync(loginId, cancellationToken).ConfigureAwait(false);
        }
        var local = await GetLocalAdminAsync(cancellationToken).ConfigureAwait(false);
        return await local.CancelLoginAsync(loginId).ConfigureAwait(false);
    }

    public async Task<ControlMigrationStart> StartMigrationAsync(
        string sourceThreadId,
        string targetAccountId,
        CancellationToken cancellationToken = default)
    {
        if (!await _control.IsAvailableAsync(TimeSpan.FromMilliseconds(800), cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Thread migration requires the active Router control plane. Start or restart Codex Desktop with Router integration first.");
        }
        return await _control.StartMigrationAsync(sourceThreadId, targetAccountId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControlMigrationStatus> GetMigrationStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (!await _control.IsAvailableAsync(TimeSpan.FromMilliseconds(800), cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Router control plane is unavailable while reading migration status.");
        }
        return await _control.MigrationStatusAsync(jobId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControlMigrationStart> RetryMigrationAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (!await _control.IsAvailableAsync(TimeSpan.FromMilliseconds(800), cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Router control plane is unavailable while retrying migration.");
        }
        return await _control.RetryMigrationAsync(jobId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControlMigrationStatus> CancelMigrationAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (!await _control.IsAvailableAsync(TimeSpan.FromMilliseconds(800), cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Router control plane is unavailable while canceling migration.");
        }
        return await _control.CancelMigrationAsync(jobId, cancellationToken).ConfigureAwait(false);
    }

    public Task<OfficialLoginBrowser> OpenLoginUrlAsync(
        string url,
        string? proxyUrl = null,
        CancellationToken cancellationToken = default)
    {
        var uri = ValidateOfficialLoginUrl(url);
        return OfficialLoginBrowser.OpenAsync(uri, proxyUrl, cancellationToken);
    }

    async Task<IAsyncDisposable> IOfficialLoginClient.OpenLoginUrlAsync(
        string url,
        string? proxyUrl,
        CancellationToken cancellationToken) =>
        await OpenLoginUrlAsync(url, proxyUrl, cancellationToken).ConfigureAwait(false);

    public static Uri ValidateOfficialLoginUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.Equals(uri.Host, "auth.openai.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Official Codex login URL is invalid.", nameof(url));
        }
        // Return the exact official URL. Router must not append, remove, or rewrite OAuth parameters.
        return uri;
    }

    public async Task<DesktopIntegrationChangeResult> EnableDesktopIntegrationAsync(
        string shimPath,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var repository = await GetRepositoryAsync(cancellationToken).ConfigureAwait(false);
        if ((await repository.ListAccountsAsync(cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            throw new InvalidOperationException("Add at least one account before enabling Codex Desktop integration.");
        }
        return await _integration.EnableAsync(shimPath, force, cancellationToken).ConfigureAwait(false);
    }

    public Task<DesktopIntegrationChangeResult> DisableDesktopIntegrationAsync(
        bool force = false,
        CancellationToken cancellationToken = default) =>
        _integration.DisableAsync(force, cancellationToken);

    public DesktopIntegrationProbe ProbeDesktopIntegration(string shimPath) => _integration.Probe(shimPath);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_localAdmin is not null)
        {
            await _localAdmin.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<RouterRepository> GetRepositoryAsync(CancellationToken cancellationToken)
    {
        if (_repository is not null) return _repository;
        _database = new StorageDatabase(new StorageOptions(_paths.DatabasePath));
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _repository = new RouterRepository(_database);
        return _repository;
    }

    private async Task EnsureLocalAdminSafeAsync(CancellationToken cancellationToken)
    {
        var shimPath = ResolveShimCandidate();
        var probe = _integration.Probe(shimPath);
        if (probe.Status == DesktopIntegrationStatus.Active)
        {
            var desktop = await new CodexDesktopDiscovery().DiscoverAsync(cancellationToken).ConfigureAwait(false);
            if (desktop.IsRunning)
            {
                throw new InvalidOperationException(
                    "Codex Router control pipe is unavailable while Desktop integration is active. Refusing to start a second account worker; restart Codex Desktop or disable integration first.");
            }
        }
    }

    private async Task<LocalAccountAdmin> GetLocalAdminAsync(CancellationToken cancellationToken)
    {
        if (_localAdmin is not null) return _localAdmin;
        var repository = await GetRepositoryAsync(cancellationToken).ConfigureAwait(false);
        _localAdmin = await LocalAccountAdmin.CreateAsync(_paths, repository, cancellationToken).ConfigureAwait(false);
        return _localAdmin;
    }

    private static string ResolveShimCandidate()
    {
        var baseDirectory = AppContext.BaseDirectory;
        return Path.Combine(baseDirectory, "codex-route.exe");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class LocalAccountAdmin : IAsyncDisposable
    {
        private readonly RouterPaths _paths;
        private readonly WorkerPool _pool;
        private readonly ProfileMaterializer _materializer;
        private readonly string _nativeCodexExecutable;
        private readonly string? _desktopExecutable;
        private readonly ICodexCliLoginRunner _cliLoginRunner = new CodexCliLoginRunner();
        private readonly ICodexDesktopLoginRunner _desktopLoginRunner = new CodexDesktopLoginRunner();
        private readonly IOfficialAppServerLoginSessionFactory _officialBrowserLoginFactory = new OfficialAppServerLoginSessionFactory();
        private readonly ICodexCredentialWriter _credentialWriter;
        private readonly Dictionary<string, LocalBrowserLogin> _browserLogins = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LocalNativeLogin> _logins = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _disposeCts = new();

        private LocalAccountAdmin(
            RouterPaths paths,
            WorkerPool pool,
            ProfileMaterializer materializer,
            AccountService accounts,
            string nativeCodexExecutable,
            string? desktopExecutable,
            ICodexCredentialWriter credentialWriter)
        {
            _paths = paths;
            _pool = pool;
            _materializer = materializer;
            Accounts = accounts;
            _nativeCodexExecutable = nativeCodexExecutable;
            _desktopExecutable = desktopExecutable;
            _credentialWriter = credentialWriter;
        }

        public AccountService Accounts { get; }

        public static async Task<LocalAccountAdmin> CreateAsync(
            RouterPaths paths,
            RouterRepository repository,
            CancellationToken cancellationToken)
        {
            var discovery = await new NativeCodexLocator(paths).DiscoverAsync(cancellationToken).ConfigureAwait(false);
            if (!discovery.Succeeded || discovery.Binary is null)
            {
                throw new FileNotFoundException(discovery.Error ?? "Real Codex CLI could not be discovered.");
            }
            var pool = new WorkerPool(new CodexAppServerWorkerFactory(discovery.Binary.Path));
            var materializer = new ProfileMaterializer(new ProfileLayout(paths.Root));
            var accounts = new AccountService(repository, pool, materializer,
                options: new AccountServiceOptions(
                    LoginTimeout: TimeSpan.FromMinutes(10),
                    EnableQuotaBackgroundRefresh: false));
            var credentialWriter = new CodexDirectKeyringStore();
            _ = await PendingOnboardingCleanup.CleanupAsync(
                repository,
                accounts,
                credentialWriter,
                cancellationToken).ConfigureAwait(false);
            var desktopDiscovery = await new CodexDesktopDiscovery().DiscoverAsync(cancellationToken).ConfigureAwait(false);
            var desktopExecutable = desktopDiscovery.RunningProcesses
                .FirstOrDefault(static process => process.HasMainWindow && !string.IsNullOrWhiteSpace(process.ExecutablePath))
                ?.ExecutablePath;
            return new LocalAccountAdmin(
                paths,
                pool,
                materializer,
                accounts,
                discovery.Binary.Path,
                desktopExecutable,
                credentialWriter);
        }

        public Task<ChatGptSessionOnboardingResult> ImportChatGptSessionAsync(
            string alias,
            string sessionJson,
            string? proxyUrl,
            CancellationToken cancellationToken)
        {
            var onboarding = new ChatGptSessionAgentIdentityOnboarding(
                Accounts,
                _materializer,
                ResolveDefaultCodexHome(),
                credentialWriter: _credentialWriter);
            return onboarding.ImportAsync(alias, sessionJson, proxyUrl, cancellationToken);
        }

        public async Task<ControlLoginStart> StartOnboardAsync(
            string alias,
            string loginMethod,
            string? proxyUrl,
            CancellationToken cancellationToken)
        {
            loginMethod = loginMethod.Trim().ToLowerInvariant();
            if (loginMethod is not (ControlLoginMethods.Desktop or ControlLoginMethods.Browser or ControlLoginMethods.Device))
            {
                throw new ArgumentException("Login method must be desktop, browser, or device.", nameof(loginMethod));
            }
            if (loginMethod == ControlLoginMethods.Desktop && string.IsNullOrWhiteSpace(_desktopExecutable))
            {
                throw new InvalidOperationException("Official Codex Desktop executable could not be discovered for isolated Desktop login.");
            }

            var source = ResolveDefaultCodexHome();
            var template = await _materializer.ImportSharedTemplateAsync(source, cancellationToken).ConfigureAwait(false);
            var profile = await Accounts.CreateAccountProfileAsync(
                alias,
                template,
                enabled: false,
                lifecycle: AccountLifecycle.Pending,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            try
            {
                await ProfileWorkerNetworkRoute.SaveProxyAsync(profile.CodexHome, proxyUrl, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception routeFailure)
            {
                var rollbackError = await PendingOnboardingRollback.TryRollbackAsync(
                    Accounts, _credentialWriter, profile).ConfigureAwait(false);
                if (rollbackError is null) throw;
                throw new InvalidOperationException($"{routeFailure.Message}; {rollbackError}", routeFailure);
            }
            if (loginMethod is ControlLoginMethods.Browser or ControlLoginMethods.Device)
            {
                IOfficialAppServerLoginSession session;
                try
                {
                    session = await _officialBrowserLoginFactory.StartAsync(
                        _nativeCodexExecutable,
                        profile,
                        proxyUrl,
                        deviceCode: loginMethod == ControlLoginMethods.Device,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception startFailure)
                {
                    var rollbackError = await PendingOnboardingRollback.TryRollbackAsync(
                        Accounts, _credentialWriter, profile).ConfigureAwait(false);
                    if (rollbackError is null) throw;
                    throw new InvalidOperationException($"{startFailure.Message}; {rollbackError}", startFailure);
                }

                var appServerLoginId = $"codex-{loginMethod}-{Guid.NewGuid():N}";
                var appServerOperation = new LocalBrowserLogin(
                    profile.Id,
                    session,
                    CompleteBrowserOperationAsync(appServerLoginId, profile, session));
                _browserLogins.Add(appServerLoginId, appServerOperation);
                return new ControlLoginStart(
                    profile.Id.Value,
                    appServerLoginId,
                    session.AuthUrl.AbsoluteUri,
                    session.StartedAt,
                    loginMethod,
                    session.UserCode);
            }

            var loginId = $"codex-{loginMethod}-{Guid.NewGuid():N}";
            var userCancel = new CancellationTokenSource();
            var operation = new LocalNativeLogin(
                profile.Id,
                userCancel,
                CompleteNativeOperationAsync(loginId, profile, loginMethod, proxyUrl, userCancel.Token));
            _logins.Add(loginId, operation);
            return new ControlLoginStart(profile.Id.Value, loginId, null, DateTimeOffset.UtcNow, loginMethod);
        }

        public async Task<ControlLoginStatus> GetLoginStatusAsync(string loginId)
        {
            if (_browserLogins.TryGetValue(loginId, out var browserOperation))
            {
                if (!browserOperation.Completion.IsCompleted)
                {
                    return new ControlLoginStatus(loginId, "pending", browserOperation.AccountId.Value, null, null, null, DateTimeOffset.UtcNow);
                }
                var browserStatus = await browserOperation.Completion.ConfigureAwait(false);
                if (_browserLogins.Remove(loginId, out var removedBrowser))
                {
                    await removedBrowser.DisposeAsync().ConfigureAwait(false);
                }
                return browserStatus;
            }

            if (!_logins.TryGetValue(loginId, out var operation))
            {
                throw new KeyNotFoundException($"Login '{loginId}' is unknown.");
            }
            if (!operation.Completion.IsCompleted)
            {
                return new ControlLoginStatus(loginId, "pending", operation.AccountId.Value, null, null, null, DateTimeOffset.UtcNow);
            }
            var status = await operation.Completion.ConfigureAwait(false);
            if (_logins.Remove(loginId, out var removed))
            {
                removed.Dispose();
            }
            return status;
        }

        public async Task<ControlLoginStatus> CancelLoginAsync(string loginId)
        {
            if (_browserLogins.TryGetValue(loginId, out var browserOperation))
            {
                await browserOperation.Session.CancelAsync(CancellationToken.None).ConfigureAwait(false);
                var browserStatus = await browserOperation.Completion.ConfigureAwait(false);
                if (_browserLogins.Remove(loginId, out var removedBrowser))
                {
                    await removedBrowser.DisposeAsync().ConfigureAwait(false);
                }
                return browserStatus;
            }

            if (!_logins.TryGetValue(loginId, out var operation))
            {
                throw new KeyNotFoundException($"Login '{loginId}' is unknown.");
            }
            operation.Cancel();
            var status = await operation.Completion.ConfigureAwait(false);
            if (_logins.Remove(loginId, out var removed))
            {
                removed.Dispose();
            }
            return status;
        }

        public async ValueTask DisposeAsync()
        {
            _disposeCts.Cancel();
            foreach (var login in _browserLogins.Values)
            {
                try { await login.Session.CancelAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            }
            var browserCompletions = _browserLogins.Values.Select(static login => login.Completion).ToArray();
            if (browserCompletions.Length > 0)
            {
                try { await Task.WhenAll(browserCompletions).ConfigureAwait(false); } catch { }
            }
            foreach (var login in _browserLogins.Values) await login.DisposeAsync().ConfigureAwait(false);
            _browserLogins.Clear();
            foreach (var login in _logins.Values) login.Cancel();
            var completions = _logins.Values.Select(static login => login.Completion).ToArray();
            if (completions.Length > 0)
            {
                try { await Task.WhenAll(completions).ConfigureAwait(false); } catch { }
            }
            foreach (var login in _logins.Values) login.Dispose();
            _logins.Clear();
            await Accounts.DisposeAsync().ConfigureAwait(false);
            await _pool.DisposeAsync().ConfigureAwait(false);
            _disposeCts.Dispose();
        }

        private async Task<ControlLoginStatus> CompleteBrowserOperationAsync(
            string loginId,
            AccountProfile profile,
            IOfficialAppServerLoginSession session)
        {
            ControlLoginStatus status;
            try
            {
                var completion = await session.WaitForCompletionAsync(TimeSpan.FromMinutes(10), _disposeCts.Token).ConfigureAwait(false);
                if (!completion.Success)
                {
                    throw new AccountServiceException(completion.Error ?? "Official Codex ChatGPT login failed.");
                }

                await session.DisposeAsync().ConfigureAwait(false);
                var verified = await Accounts.CompletePendingExternalLoginAsync(profile.Id, _disposeCts.Token).ConfigureAwait(false);
                try { _ = await Accounts.RefreshQuotaAsync(verified.Id, _disposeCts.Token).ConfigureAwait(false); } catch { }
                status = new ControlLoginStatus(loginId, "completed", verified.Id.Value, verified.Email, verified.PlanType, null, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
                status = new ControlLoginStatus(loginId, "failed", profile.Id.Value, null, null, "Official Codex login was interrupted.", DateTimeOffset.UtcNow);
            }
            catch (TimeoutException)
            {
                status = new ControlLoginStatus(loginId, "failed", profile.Id.Value, null, null, "Official Codex login timed out.", DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                status = new ControlLoginStatus(loginId, "failed", profile.Id.Value, null, null, ex.Message, DateTimeOffset.UtcNow);
            }
            finally
            {
                try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
            }

            if (status.State == "failed")
            {
                var rollbackError = await PendingOnboardingRollback.TryRollbackAsync(
                    Accounts, _credentialWriter, profile).ConfigureAwait(false);
                if (rollbackError is not null)
                {
                    status = status with { Error = $"{status.Error ?? "Login failed"}; {rollbackError}" };
                }
            }
            return status;
        }

        private async Task<ControlLoginStatus> CompleteNativeOperationAsync(
            string loginId,
            AccountProfile profile,
            string loginMethod,
            string? proxyUrl,
            CancellationToken userCancellationToken)
        {
            ControlLoginStatus status;
            using var loginCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token, userCancellationToken);
            loginCts.CancelAfter(TimeSpan.FromMinutes(10));
            try
            {
                bool loginSucceeded;
                string? loginError;
                if (loginMethod == ControlLoginMethods.Desktop)
                {
                    var result = await _desktopLoginRunner.RunAsync(
                        _desktopExecutable!,
                        _nativeCodexExecutable,
                        profile.CodexHome,
                        TimeSpan.FromMinutes(10),
                        proxyUrl,
                        loginCts.Token).ConfigureAwait(false);
                    loginSucceeded = result.Succeeded;
                    loginError = result.Error;
                }
                else
                {
                    var result = await _cliLoginRunner.RunAsync(
                        _nativeCodexExecutable,
                        profile.CodexHome,
                        deviceAuth: loginMethod == ControlLoginMethods.Device,
                        proxyUrl: proxyUrl,
                        cancellationToken: loginCts.Token).ConfigureAwait(false);
                    loginSucceeded = result.Succeeded;
                    loginError = result.Error;
                }
                if (!loginSucceeded)
                {
                    throw new AccountServiceException(loginError ?? "Official Codex login failed.");
                }

                var verified = await Accounts.CompletePendingExternalLoginAsync(profile.Id, loginCts.Token).ConfigureAwait(false);
                try { _ = await Accounts.RefreshQuotaAsync(verified.Id, loginCts.Token).ConfigureAwait(false); } catch { }
                status = new ControlLoginStatus(loginId, "completed", verified.Id.Value, verified.Email, verified.PlanType, null, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
                status = new ControlLoginStatus(loginId, "failed", profile.Id.Value, null, null, "Official Codex login was interrupted.", DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (userCancellationToken.IsCancellationRequested)
            {
                status = new ControlLoginStatus(loginId, "failed", profile.Id.Value, null, null, "Official Codex login was canceled.", DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException)
            {
                status = new ControlLoginStatus(loginId, "failed", profile.Id.Value, null, null, "Official Codex login timed out.", DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                status = new ControlLoginStatus(loginId, "failed", profile.Id.Value, null, null, ex.Message, DateTimeOffset.UtcNow);
            }

            if (status.State == "failed")
            {
                var rollbackError = await PendingOnboardingRollback.TryRollbackAsync(
                    Accounts, _credentialWriter, profile).ConfigureAwait(false);
                if (rollbackError is not null)
                {
                    status = status with { Error = $"{status.Error ?? "Login failed"}; {rollbackError}" };
                }
            }
            return status;
        }

        private static string ResolveDefaultCodexHome()
        {
            var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        private sealed class LocalBrowserLogin : IAsyncDisposable
        {
            public LocalBrowserLogin(AccountId accountId, IOfficialAppServerLoginSession session, Task<ControlLoginStatus> completion)
            {
                AccountId = accountId;
                Session = session;
                Completion = completion;
            }

            public AccountId AccountId { get; }
            public IOfficialAppServerLoginSession Session { get; }
            public Task<ControlLoginStatus> Completion { get; }
            public ValueTask DisposeAsync() => Session.DisposeAsync();
        }

        private sealed class LocalNativeLogin : IDisposable
        {
            public LocalNativeLogin(AccountId accountId, CancellationTokenSource cancellation, Task<ControlLoginStatus> completion)
            {
                AccountId = accountId;
                Cancellation = cancellation;
                Completion = completion;
            }

            public AccountId AccountId { get; }
            public CancellationTokenSource Cancellation { get; }
            public Task<ControlLoginStatus> Completion { get; }
            public void Cancel() => Cancellation.Cancel();
            public void Dispose() => Cancellation.Dispose();
        }
    }
}
