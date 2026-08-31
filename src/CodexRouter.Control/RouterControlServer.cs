using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CodexRouter.Accounts;
using CodexRouter.Domain;
using CodexRouter.Migration;
using CodexRouter.Storage;
using CodexRouter.Workers;

namespace CodexRouter.Control;

public sealed class RouterControlServer : IAsyncDisposable
{
    private readonly ControlEndpoint _endpoint;
    private readonly RouterRepository _repository;
    private readonly ProfileMaterializer _materializer;
    private readonly AccountService _accounts;
    private readonly ThreadMigrationEngine _migration;
    private readonly string _templateSourceCodexHome;
    private readonly string? _nativeCodexExecutable;
    private readonly string? _desktopExecutable;
    private readonly ICodexCliLoginRunner _cliLoginRunner;
    private readonly ICodexDesktopLoginRunner _desktopLoginRunner;
    private readonly IOfficialAppServerLoginSessionFactory _officialBrowserLoginFactory;
    private readonly ICodexCredentialWriter _credentialWriter;
    private readonly ChatGptSessionAgentIdentityOnboarding _sessionOnboarding;
    private readonly ConcurrentDictionary<string, LoginOperation> _logins = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BrowserLoginOperation> _browserLogins = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NativeLoginOperation> _nativeLogins = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private Task? _acceptLoop;
    private string? _token;
    private int _connectionId;
    private int _disposed;

    public RouterControlServer(
        string root,
        RouterRepository repository,
        WorkerPool workerPool,
        ProfileMaterializer materializer,
        string? templateSourceCodexHome = null,
        AccountServiceOptions? accountOptions = null,
        string? nativeCodexExecutable = null,
        ICodexCliLoginRunner? cliLoginRunner = null,
        string? desktopExecutable = null,
        ICodexDesktopLoginRunner? desktopLoginRunner = null,
        IOfficialAppServerLoginSessionFactory? officialBrowserLoginFactory = null,
        IAgentIdentityRegistrar? agentIdentityRegistrar = null,
        ICodexCredentialWriter? credentialWriter = null)
    {
        _endpoint = new ControlEndpoint(root);
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
        _accounts = new AccountService(repository, workerPool, materializer, options: accountOptions);
        _migration = new ThreadMigrationEngine(repository, workerPool);
        _templateSourceCodexHome = Path.GetFullPath(templateSourceCodexHome ?? ResolveDefaultCodexHome());
        _nativeCodexExecutable = string.IsNullOrWhiteSpace(nativeCodexExecutable) ? null : Path.GetFullPath(nativeCodexExecutable);
        _desktopExecutable = string.IsNullOrWhiteSpace(desktopExecutable) ? null : Path.GetFullPath(desktopExecutable);
        _cliLoginRunner = cliLoginRunner ?? new CodexCliLoginRunner();
        _desktopLoginRunner = desktopLoginRunner ?? new CodexDesktopLoginRunner();
        _officialBrowserLoginFactory = officialBrowserLoginFactory ?? new OfficialAppServerLoginSessionFactory();
        _credentialWriter = credentialWriter ?? new CodexDirectKeyringStore();
        _sessionOnboarding = new ChatGptSessionAgentIdentityOnboarding(
            _accounts,
            _materializer,
            _templateSourceCodexHome,
            agentIdentityRegistrar,
            _credentialWriter);
    }

    public string PipeName => _endpoint.PipeName;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_acceptLoop is not null)
        {
            return;
        }
        _ = await PendingOnboardingCleanup.CleanupAsync(
            _repository,
            _accounts,
            _credentialWriter,
            cancellationToken).ConfigureAwait(false);
        _token = await _endpoint.GetOrCreateTokenAsync(cancellationToken).ConfigureAwait(false);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_disposeCts.Token), CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _disposeCts.Cancel();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        var connections = _connections.Values.ToArray();
        if (connections.Length > 0)
        {
            try { await Task.WhenAll(connections).ConfigureAwait(false); } catch { }
        }
        foreach (var operation in _logins.Values)
        {
            await operation.DisposeAsync().ConfigureAwait(false);
        }
        _logins.Clear();
        foreach (var operation in _browserLogins.Values)
        {
            try { await operation.Session.CancelAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }
        var browserCompletions = _browserLogins.Values.Select(static operation => operation.Completion).ToArray();
        if (browserCompletions.Length > 0)
        {
            try { await Task.WhenAll(browserCompletions).ConfigureAwait(false); } catch { }
        }
        foreach (var operation in _browserLogins.Values)
        {
            await operation.DisposeAsync().ConfigureAwait(false);
        }
        _browserLogins.Clear();
        foreach (var operation in _nativeLogins.Values)
        {
            operation.Cancel();
        }
        var nativeCompletions = _nativeLogins.Values.Select(static operation => operation.Completion).ToArray();
        if (nativeCompletions.Length > 0)
        {
            try { await Task.WhenAll(nativeCompletions).ConfigureAwait(false); } catch { }
        }
        foreach (var operation in _nativeLogins.Values)
        {
            operation.Dispose();
        }
        _nativeLogins.Clear();
        await _migration.DisposeAsync().ConfigureAwait(false);
        await _accounts.DisposeAsync().ConfigureAwait(false);
        _disposeCts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                _endpoint.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                pipe.Dispose();
                throw;
            }

            var id = Interlocked.Increment(ref _connectionId);
            var task = HandleConnectionAsync(pipe, cancellationToken);
            _connections[id] = task;
            _ = task.ContinueWith(
                completed => _connections.TryRemove(id, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using var ownedPipe = pipe;
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 16 * 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        JsonDocument? request = null;
        try
        {
            request = JsonDocument.Parse(line);
            var root = request.RootElement;
            var id = root.TryGetProperty("id", out var idElement) ? idElement.Clone() : default;
            var token = root.TryGetProperty("token", out var tokenElement) && tokenElement.ValueKind == JsonValueKind.String
                ? tokenElement.GetString()
                : null;
            if (!CryptographicEquals(token, _token))
            {
                await WriteErrorAsync(writer, id, 401, "unauthorized", cancellationToken).ConfigureAwait(false);
                return;
            }
            if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            {
                await WriteErrorAsync(writer, id, 400, "missing method", cancellationToken).ConfigureAwait(false);
                return;
            }
            var method = methodElement.GetString()!;
            var parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement.Clone() : EmptyObject();
            try
            {
                var result = await DispatchAsync(method, parameters, cancellationToken).ConfigureAwait(false);
                await WriteResultAsync(writer, id, result, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(writer, id, 500, ex.Message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (JsonException ex)
        {
            await WriteErrorAsync(writer, default, 400, $"invalid json: {ex.Message}", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            request?.Dispose();
        }
    }

    private async Task<JsonElement> DispatchAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        return method switch
        {
            "snapshot" => JsonFrom(await BuildSnapshotAsync(cancellationToken).ConfigureAwait(false)),
            "router/mode" => JsonFrom(await ChangeModeAsync(parameters, cancellationToken).ConfigureAwait(false)),
            "account/refreshQuota" => JsonFrom(await RefreshQuotaAsync(parameters, cancellationToken).ConfigureAwait(false)),
            "account/onboard/start" => JsonFrom(await StartOnboardAsync(parameters, cancellationToken).ConfigureAwait(false)),
            "account/session/import" => JsonFrom(await ImportChatGptSessionAsync(parameters, cancellationToken).ConfigureAwait(false)),
            "account/login/start" => JsonFrom(await StartExistingLoginAsync(parameters, cancellationToken).ConfigureAwait(false)),
            "account/login/status" => JsonFrom(await GetLoginStatusAsync(parameters, cancellationToken).ConfigureAwait(false)),
            "account/login/cancel" => JsonFrom(await CancelLoginAsync(parameters, cancellationToken).ConfigureAwait(false)),
            "account/rename" => JsonFrom(await RenameAsync(parameters, cancellationToken).ConfigureAwait(false)),
            "account/enable" => JsonFrom(await EnableAsync(parameters, cancellationToken).ConfigureAwait(false)),
            "account/delete" => JsonFrom(await DeleteAsync(parameters, cancellationToken).ConfigureAwait(false)),
            "migration/start" => JsonFrom(await StartMigrationAsync(parameters, cancellationToken).ConfigureAwait(false)),
            "migration/status" => JsonFrom(await MigrationStatusAsync(parameters, cancellationToken).ConfigureAwait(false)),
            "migration/retry" => JsonFrom(await RetryMigrationAsync(parameters, cancellationToken).ConfigureAwait(false)),
            "migration/cancel" => JsonFrom(await CancelMigrationAsync(parameters, cancellationToken).ConfigureAwait(false)),
            _ => throw new InvalidOperationException($"Unknown control method '{method}'.")
        };
    }

    private async Task<ControlSnapshot> BuildSnapshotAsync(CancellationToken cancellationToken)
    {
        var settings = await _repository.GetRouterSettingsAsync(cancellationToken).ConfigureAwait(false);
        var current = await _repository.GetRuntimeStateAsync("front_account_id", cancellationToken).ConfigureAwait(false);
        var currentThread = await _repository.GetRuntimeStateAsync("front_thread_id", cancellationToken).ConfigureAwait(false);
        var accounts = await _repository.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        var views = new List<ControlAccountView>(accounts.Count);
        foreach (var stored in accounts)
        {
            var quota = await _repository.GetLatestQuotaSnapshotAsync(stored.Profile.Id, cancellationToken).ConfigureAwait(false);
            var health = (await _repository.GetHealthEventsAsync(stored.Profile.Id, 1, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault()?.Health;
            views.Add(new ControlAccountView(
                stored.Profile.Id.Value,
                stored.Profile.Alias,
                stored.Profile.Email,
                stored.Profile.PlanType,
                stored.Profile.Enabled,
                stored.Profile.Priority,
                (health?.State ?? AccountHealthState.Unknown).ToString(),
                health?.Reason,
                string.Equals(current?.Value, stored.Profile.Id.Value, StringComparison.Ordinal),
                quota?.FetchedAt,
                quota?.Buckets.Select(bucket => new ControlQuotaBucket(
                    bucket.LimitId,
                    bucket.LimitName,
                    bucket.Slot.ToString(),
                    bucket.UsedPercent,
                    bucket.RemainingPercent,
                    bucket.WindowDuration?.TotalMinutes,
                    bucket.ResetsAt)).ToArray() ?? Array.Empty<ControlQuotaBucket>()));
        }

        return new ControlSnapshot(
            settings.Mode.ToString(),
            settings.PinnedAccountId?.Value,
            current?.Value,
            currentThread?.Value,
            views,
            DateTimeOffset.UtcNow);
    }

    private async Task<ControlModeChange> ChangeModeAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var modeText = RequiredString(parameters, "mode");
        var current = await _repository.GetRouterSettingsAsync(cancellationToken).ConfigureAwait(false);
        RouterSettings next = modeText.ToLowerInvariant() switch
        {
            "auto" => current with { Mode = RouterMode.Auto, PinnedAccountId = null, UpdatedAt = DateTimeOffset.UtcNow },
            "off" => current with { Mode = RouterMode.Off, PinnedAccountId = null, UpdatedAt = DateTimeOffset.UtcNow },
            "pinned" => current with
            {
                Mode = RouterMode.Pinned,
                PinnedAccountId = new AccountId(RequiredString(parameters, "accountId")),
                UpdatedAt = DateTimeOffset.UtcNow
            },
            _ => throw new ArgumentException("mode must be auto, pinned, or off")
        };
        if (next.PinnedAccountId is { } pinned && await _repository.GetAccountAsync(pinned, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException($"Pinned account '{pinned}' does not exist.");
        }
        await _repository.UpdateRouterSettingsAsync(next, cancellationToken).ConfigureAwait(false);
        return new ControlModeChange(next.Mode.ToString(), next.PinnedAccountId?.Value);
    }

    private async Task<QuotaSnapshot> RefreshQuotaAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        return await _accounts.RefreshQuotaAsync(
            new AccountId(RequiredString(parameters, "accountId")),
            cancellationToken).ConfigureAwait(false);
    }

    private Task<ChatGptSessionOnboardingResult> ImportChatGptSessionAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var alias = RequiredString(parameters, "alias");
        var sessionJson = RequiredString(parameters, "sessionJson");
        var proxyUrl = OptionalString(parameters, "proxyUrl");
        return _sessionOnboarding.ImportAsync(alias, sessionJson, proxyUrl, cancellationToken);
    }

    private async Task<ControlLoginStart> StartOnboardAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var alias = RequiredString(parameters, "alias");
        var loginMethod = OptionalString(parameters, "loginMethod")?.ToLowerInvariant() ?? ControlLoginMethods.Browser;
        var proxyUrl = CodexLoginProxy.Normalize(OptionalString(parameters, "proxyUrl"));
        if (loginMethod is not (ControlLoginMethods.Desktop or ControlLoginMethods.Browser or ControlLoginMethods.Device))
        {
            throw new ArgumentException("loginMethod must be desktop, browser, or device.");
        }
        if (string.IsNullOrWhiteSpace(_nativeCodexExecutable))
        {
            throw new InvalidOperationException("Official Codex CLI path is unavailable for account login.");
        }
        if (loginMethod == ControlLoginMethods.Desktop && string.IsNullOrWhiteSpace(_desktopExecutable))
        {
            throw new InvalidOperationException("Official Codex Desktop executable path is unavailable for Desktop login.");
        }
        if (!Directory.Exists(_templateSourceCodexHome))
        {
            throw new DirectoryNotFoundException($"Template source CODEX_HOME does not exist: {_templateSourceCodexHome}");
        }

        var template = await _materializer.ImportSharedTemplateAsync(_templateSourceCodexHome, cancellationToken).ConfigureAwait(false);
        var profile = await _accounts.CreateAccountProfileAsync(
            alias,
            template,
            enabled: false,
            lifecycle: AccountLifecycle.Pending,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            // Login and every later worker for this isolated profile must use the same
            // explicitly selected route. Null means explicit direct mode, not inheritance.
            await ProfileWorkerNetworkRoute.SaveProxyAsync(profile.CodexHome, proxyUrl, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception routeFailure)
        {
            var rollbackError = await PendingOnboardingRollback.TryRollbackAsync(
                _accounts, _credentialWriter, profile).ConfigureAwait(false);
            if (rollbackError is null) throw;
            throw new InvalidOperationException($"{routeFailure.Message}; {rollbackError}", routeFailure);
        }
        if (loginMethod is ControlLoginMethods.Browser or ControlLoginMethods.Device)
        {
            IOfficialAppServerLoginSession session;
            try
            {
                session = await _officialBrowserLoginFactory.StartAsync(
                    _nativeCodexExecutable!,
                    profile,
                    proxyUrl,
                    deviceCode: loginMethod == ControlLoginMethods.Device,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception startFailure)
            {
                var rollbackError = await PendingOnboardingRollback.TryRollbackAsync(
                    _accounts, _credentialWriter, profile).ConfigureAwait(false);
                if (rollbackError is null) throw;
                throw new InvalidOperationException($"{startFailure.Message}; {rollbackError}", startFailure);
            }

            var appServerLoginId = $"codex-{loginMethod}-{Guid.NewGuid():N}";
            var appServerOperation = new BrowserLoginOperation(
                profile.Id,
                session,
                CompleteBrowserLoginOperationAsync(appServerLoginId, profile, session));
            if (!_browserLogins.TryAdd(appServerLoginId, appServerOperation))
            {
                await appServerOperation.DisposeAsync().ConfigureAwait(false);
                var rollbackError = await PendingOnboardingRollback.TryRollbackAsync(
                    _accounts, _credentialWriter, profile).ConfigureAwait(false);
                throw new InvalidOperationException(rollbackError is null
                    ? $"Login id '{appServerLoginId}' already exists."
                    : $"Login id '{appServerLoginId}' already exists; {rollbackError}");
            }
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
        var operation = new NativeLoginOperation(
            profile.Id,
            userCancel,
            CompleteNativeLoginOperationAsync(loginId, profile, loginMethod, proxyUrl, userCancel.Token));
        if (!_nativeLogins.TryAdd(loginId, operation))
        {
            userCancel.Cancel();
            userCancel.Dispose();
            var rollbackError = await PendingOnboardingRollback.TryRollbackAsync(
                _accounts, _credentialWriter, profile).ConfigureAwait(false);
            throw new InvalidOperationException(rollbackError is null
                ? $"Login id '{loginId}' already exists."
                : $"Login id '{loginId}' already exists; {rollbackError}");
        }
        return new ControlLoginStart(profile.Id.Value, loginId, null, DateTimeOffset.UtcNow, loginMethod);
    }

    private async Task<ControlLoginStart> StartExistingLoginAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var session = await _accounts.BeginChatGptLoginAsync(
            new AccountId(RequiredString(parameters, "accountId")),
            openBrowser: false,
            cancellationToken).ConfigureAwait(false);
        return RegisterLogin(session, cleanupAccountOnFailure: false);
    }

    private ControlLoginStart RegisterLogin(ChatGptLoginSession session, bool cleanupAccountOnFailure)
    {
        var operation = new LoginOperation(session, CompleteLoginOperationAsync(session, cleanupAccountOnFailure));
        if (!_logins.TryAdd(session.LoginId, operation))
        {
            _ = operation.DisposeAsync();
            throw new InvalidOperationException($"Login id '{session.LoginId}' already exists.");
        }
        return new ControlLoginStart(
            session.AccountId.Value,
            session.LoginId,
            session.AuthUrl.AbsoluteUri,
            session.StartedAt,
            "app-server");
    }

    private async Task<ControlLoginStatus> GetLoginStatusAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var loginId = RequiredString(parameters, "loginId");
        if (_browserLogins.TryGetValue(loginId, out var browserOperation))
        {
            if (!browserOperation.Completion.IsCompleted)
            {
                return new ControlLoginStatus(loginId, "pending", browserOperation.AccountId.Value, null, null, null, DateTimeOffset.UtcNow);
            }
            var browserStatus = await browserOperation.Completion.ConfigureAwait(false);
            if (_browserLogins.TryRemove(loginId, out var removedBrowser))
            {
                await removedBrowser.DisposeAsync().ConfigureAwait(false);
            }
            return browserStatus;
        }

        if (_nativeLogins.TryGetValue(loginId, out var nativeOperation))
        {
            if (!nativeOperation.Completion.IsCompleted)
            {
                return new ControlLoginStatus(loginId, "pending", nativeOperation.AccountId.Value, null, null, null, DateTimeOffset.UtcNow);
            }
            var nativeStatus = await nativeOperation.Completion.ConfigureAwait(false);
            if (_nativeLogins.TryRemove(loginId, out var removedNative))
            {
                removedNative.Dispose();
            }
            return nativeStatus;
        }

        if (!_logins.TryGetValue(loginId, out var operation))
        {
            throw new KeyNotFoundException($"Login '{loginId}' is unknown or expired.");
        }
        if (!operation.Completion.IsCompleted)
        {
            return new ControlLoginStatus(loginId, "pending", operation.Session.AccountId.Value, null, null, null, DateTimeOffset.UtcNow);
        }

        var status = await operation.Completion.ConfigureAwait(false);
        if (_logins.TryRemove(loginId, out var removed))
        {
            await removed.DisposeAsync().ConfigureAwait(false);
        }
        return status;
    }

    private async Task<ControlLoginStatus> CancelLoginAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var loginId = RequiredString(parameters, "loginId");
        if (_browserLogins.TryGetValue(loginId, out var browserOperation))
        {
            await browserOperation.Session.CancelAsync(cancellationToken).ConfigureAwait(false);
            var status = await browserOperation.Completion.ConfigureAwait(false);
            if (_browserLogins.TryRemove(loginId, out var removedBrowser))
            {
                await removedBrowser.DisposeAsync().ConfigureAwait(false);
            }
            return status;
        }

        if (_nativeLogins.TryGetValue(loginId, out var nativeOperation))
        {
            nativeOperation.Cancel();
            var status = await nativeOperation.Completion.ConfigureAwait(false);
            if (_nativeLogins.TryRemove(loginId, out var removedNative))
            {
                removedNative.Dispose();
            }
            return status;
        }

        if (_logins.TryGetValue(loginId, out var operation))
        {
            await operation.Session.CancelAsync(cancellationToken).ConfigureAwait(false);
            var status = await operation.Completion.ConfigureAwait(false);
            if (_logins.TryRemove(loginId, out var removed))
            {
                await removed.DisposeAsync().ConfigureAwait(false);
            }
            return status;
        }

        throw new KeyNotFoundException($"Login '{loginId}' is unknown or expired.");
    }

    private async Task<ControlLoginStatus> CompleteBrowserLoginOperationAsync(
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

            // Release the onboarding app-server before starting the normal validation worker.
            // Two app-servers sharing one CODEX_HOME can contend on Codex local databases/locks.
            await session.DisposeAsync().ConfigureAwait(false);
            var verified = await _accounts.CompletePendingExternalLoginAsync(profile.Id, _disposeCts.Token).ConfigureAwait(false);
            try { _ = await _accounts.RefreshQuotaAsync(verified.Id, _disposeCts.Token).ConfigureAwait(false); } catch { }
            status = new ControlLoginStatus(
                loginId,
                "completed",
                verified.Id.Value,
                verified.Email,
                verified.PlanType,
                null,
                DateTimeOffset.UtcNow);
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
                _accounts, _credentialWriter, profile).ConfigureAwait(false);
            if (rollbackError is not null)
            {
                status = status with { Error = $"{status.Error ?? "Login failed"}; {rollbackError}" };
            }
        }
        return status;
    }

    private async Task<ControlLoginStatus> CompleteNativeLoginOperationAsync(
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
                    _nativeCodexExecutable!,
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
                    _nativeCodexExecutable!,
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

            var verified = await _accounts.CompletePendingExternalLoginAsync(profile.Id, loginCts.Token).ConfigureAwait(false);
            try { _ = await _accounts.RefreshQuotaAsync(verified.Id, loginCts.Token).ConfigureAwait(false); } catch { }
            status = new ControlLoginStatus(
                loginId,
                "completed",
                verified.Id.Value,
                verified.Email,
                verified.PlanType,
                null,
                DateTimeOffset.UtcNow);
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
                _accounts, _credentialWriter, profile).ConfigureAwait(false);
            if (rollbackError is not null)
            {
                status = status with { Error = $"{status.Error ?? "Login failed"}; {rollbackError}" };
            }
        }
        return status;
    }

    private async Task<ControlLoginStatus> CompleteLoginOperationAsync(
        ChatGptLoginSession session,
        bool cleanupAccountOnFailure)
    {
        var status = await CompleteLoginAsync(session).ConfigureAwait(false);
        await session.DisposeAsync().ConfigureAwait(false);
        if (status.State != "failed" || !cleanupAccountOnFailure)
        {
            return status;
        }

        try
        {
            await _accounts.DeleteAccountAsync(session.AccountId, force: true, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            status = status with { Error = $"{status.Error ?? "Login failed"}; onboarding rollback failed: {ex.Message}" };
        }
        return status;
    }

    private async Task<ControlLoginStatus> CompleteLoginAsync(ChatGptLoginSession session)
    {
        try
        {
            var profile = await _accounts.CompleteChatGptLoginAsync(session, cancellationToken: _disposeCts.Token).ConfigureAwait(false);
            try { _ = await _accounts.RefreshQuotaAsync(profile.Id, _disposeCts.Token).ConfigureAwait(false); } catch { }
            return new ControlLoginStatus(
                session.LoginId,
                "completed",
                profile.Id.Value,
                profile.Email,
                profile.PlanType,
                null,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return new ControlLoginStatus(
                session.LoginId,
                "failed",
                session.AccountId.Value,
                null,
                null,
                ex.Message,
                DateTimeOffset.UtcNow);
        }
    }

    private async Task<AccountProfile> RenameAsync(JsonElement parameters, CancellationToken cancellationToken) =>
        await _accounts.RenameAsync(
            new AccountId(RequiredString(parameters, "accountId")),
            RequiredString(parameters, "alias"),
            cancellationToken).ConfigureAwait(false);

    private async Task<AccountProfile> EnableAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("enabled", out var enabledElement) || enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException("enabled must be boolean");
        }
        return await _accounts.SetEnabledAsync(
            new AccountId(RequiredString(parameters, "accountId")),
            enabledElement.GetBoolean(),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> DeleteAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var force = parameters.TryGetProperty("force", out var forceElement) && forceElement.ValueKind == JsonValueKind.True;
        await _accounts.DeleteAccountAsync(
            new AccountId(RequiredString(parameters, "accountId")),
            force,
            cancellationToken).ConfigureAwait(false);
        return new { deleted = true };
    }

    private async Task<ControlMigrationStart> StartMigrationAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var sourceThreadId = new ThreadId(RequiredString(parameters, "sourceThreadId"));
        var targetAccountId = new AccountId(RequiredString(parameters, "targetAccountId"));
        await RefreshMigrationTargetQuotaAsync(targetAccountId, cancellationToken).ConfigureAwait(false);
        var result = await _migration.QueueAsync(
            sourceThreadId,
            targetAccountId,
            cancellationToken).ConfigureAwait(false);
        return new ControlMigrationStart(result.JobId, result.State.ToString());
    }

    private async Task<ControlMigrationStatus> MigrationStatusAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var job = await _migration.GetAsync(RequiredString(parameters, "jobId"), cancellationToken).ConfigureAwait(false);
        return ToControlMigrationStatus(job);
    }

    private async Task<ControlMigrationStart> RetryMigrationAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var jobId = RequiredString(parameters, "jobId");
        var job = await _migration.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        await RefreshMigrationTargetQuotaAsync(job.TargetAccountId, cancellationToken).ConfigureAwait(false);
        var result = await _migration.QueueRetryAsync(jobId, cancellationToken).ConfigureAwait(false);
        return new ControlMigrationStart(result.JobId, result.State.ToString());
    }

    private async Task RefreshMigrationTargetQuotaAsync(AccountId targetAccountId, CancellationToken cancellationToken)
    {
        try
        {
            await _accounts.RefreshQuotaAsync(targetAccountId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ThreadMigrationException(
                $"Could not refresh quota for target account '{targetAccountId}'. Migration was not started.",
                ex);
        }
    }

    private async Task<ControlMigrationStatus> CancelMigrationAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var jobId = RequiredString(parameters, "jobId");
        await _migration.CancelAsync(jobId, cancellationToken).ConfigureAwait(false);
        return ToControlMigrationStatus(await _migration.GetAsync(jobId, cancellationToken).ConfigureAwait(false));
    }

    private static ControlMigrationStatus ToControlMigrationStatus(ThreadMigrationJob job) =>
        new(
            job.Id,
            job.SourceThreadId.Value,
            job.SourceAccountId.Value,
            job.TargetAccountId.Value,
            job.TargetThreadId?.Value,
            job.State.ToString(),
            job.Error,
            job.UpdatedAt);

    private static string RequiredString(JsonElement parameters, string name)
    {
        return OptionalString(parameters, name) ?? throw new ArgumentException($"'{name}' is required.");
    }

    private static string? OptionalString(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            return null;
        }
        return value.GetString();
    }

    private async Task WriteResultAsync(StreamWriter writer, JsonElement id, JsonElement result, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new ControlResponseEnvelope(id, result, null), _json);
        await writer.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteErrorAsync(StreamWriter writer, JsonElement id, int code, string message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new ControlResponseEnvelope(id, null, new ControlError(code, message)), _json);
        await writer.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static bool CryptographicEquals(string? left, string? right)
    {
        if (left is null || right is null) return false;
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static JsonElement JsonFrom<T>(T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return document.RootElement.Clone();
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static string ResolveDefaultCodexHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".codex");
    }

    private sealed record ControlError(int Code, string Message);
    private sealed record ControlResponseEnvelope(JsonElement Id, JsonElement? Result, ControlError? Error);

    private sealed class BrowserLoginOperation : IAsyncDisposable
    {
        public BrowserLoginOperation(AccountId accountId, IOfficialAppServerLoginSession session, Task<ControlLoginStatus> completion)
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

    private sealed class NativeLoginOperation : IDisposable
    {
        public NativeLoginOperation(AccountId accountId, CancellationTokenSource cancellation, Task<ControlLoginStatus> completion)
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

    private sealed class LoginOperation : IAsyncDisposable
    {
        public LoginOperation(ChatGptLoginSession session, Task<ControlLoginStatus> completion)
        {
            Session = session;
            Completion = completion;
        }
        public ChatGptLoginSession Session { get; }
        public Task<ControlLoginStatus> Completion { get; }
        public async ValueTask DisposeAsync() => await Session.DisposeAsync().ConfigureAwait(false);
    }
}
