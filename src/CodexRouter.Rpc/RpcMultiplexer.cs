using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodexRouter.Domain;
using CodexRouter.Routing;
using CodexRouter.Storage;
using CodexRouter.Workers;

namespace CodexRouter.Rpc;

public sealed record RpcMultiplexerOptions(
    string UserAgent = "codex-router/0.1.0",
    int MaxConcurrentFrontRequests = 64,
    TimeSpan? RequestTimeout = null)
{
    public TimeSpan EffectiveRequestTimeout => RequestTimeout ?? TimeSpan.FromSeconds(30);
}

public sealed class RpcMultiplexer : IAsyncDisposable
{
    private readonly RouterRepository _repository;
    private readonly WorkerPool _workerPool;
    private readonly RouterCoordinator _router;
    private readonly FrontAccountProjection _projection;
    private readonly WorkerClientContext _workerClientContext;
    private readonly RpcMultiplexerOptions _options;
    private readonly RpcWorkerAccess _workerAccess;
    private readonly ThreadListAggregator _threadList;
    private readonly QuotaRefreshProvider _quotaRefreshProvider;
    private readonly ConcurrentDictionary<string, IAppServerWorker> _registeredWorkers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingServerRequest> _serverRequests = new(StringComparer.Ordinal);
    private readonly Channel<WorkerEvent> _workerEvents = Channel.CreateUnbounded<WorkerEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _requestConcurrency;
    private readonly CancellationTokenSource _disposeCts = new();
    private TextWriter? _output;
    private Task? _workerEventLoop;
    private int _initializedRequestSeen;
    private int _initializedNotificationSeen;
    private int _disposed;

    public RpcMultiplexer(
        RouterRepository repository,
        WorkerPool workerPool,
        RouterCoordinator router,
        WorkerClientContext? workerClientContext = null,
        FrontAccountProjection? projection = null,
        RpcMultiplexerOptions? options = null,
        ThreadListAggregatorOptions? threadListOptions = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _workerPool = workerPool ?? throw new ArgumentNullException(nameof(workerPool));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _workerClientContext = workerClientContext ?? new WorkerClientContext();
        _projection = projection ?? new FrontAccountProjection(repository);
        _options = options ?? new RpcMultiplexerOptions();
        if (_options.MaxConcurrentFrontRequests < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        _requestConcurrency = new SemaphoreSlim(_options.MaxConcurrentFrontRequests, _options.MaxConcurrentFrontRequests);
        _workerAccess = new RpcWorkerAccess(repository, workerPool, router, RegisterWorker);
        _threadList = new ThreadListAggregator(repository, _workerAccess, threadListOptions);
        _quotaRefreshProvider = new QuotaRefreshProvider(repository, workerPool);
        _router.SetQuotaFreshnessProvider(_quotaRefreshProvider);
    }

    public FrontAccountProjection Projection => _projection;

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _initializedRequestSeen, 0, 0) != 0 || _output is not null)
        {
            throw new InvalidOperationException("This RpcMultiplexer instance can only run one front connection.");
        }

        _output = output;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        _workerEventLoop = Task.Run(() => ProcessWorkerEventsAsync(linked.Token), CancellationToken.None);
        _ = PrefetchStaleQuotasAsync(linked.Token);
        var inflight = new ConcurrentDictionary<long, Task>();
        long sequence = 0;

        try
        {
            while (!linked.Token.IsCancellationRequested)
            {
                var line = await input.ReadLineAsync(linked.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonDocument? document = null;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException ex)
                {
                    await WriteErrorAsync(default, -32700, $"Parse error: {ex.Message}", null, linked.Token).ConfigureAwait(false);
                    continue;
                }

                using (document)
                {
                    var message = document.RootElement.Clone();
                    if (ShouldProcessInline(message))
                    {
                        await ProcessFrontMessageAsync(message, linked.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        var taskId = Interlocked.Increment(ref sequence);
                        var task = ProcessFrontMessageAsync(message, linked.Token);
                        inflight[taskId] = task;
                        _ = task.ContinueWith(
                            completed => inflight.TryRemove(taskId, out _),
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                }
            }

            while (inflight.Count > 0)
            {
                var snapshot = inflight.Values.ToArray();
                if (snapshot.Length == 0)
                {
                    break;
                }
                await Task.WhenAll(snapshot).ConfigureAwait(false);
            }
        }
        finally
        {
            linked.Cancel();
            _workerEvents.Writer.TryComplete();
            if (_workerEventLoop is not null)
            {
                try { await _workerEventLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
    }

    public async Task ProcessFrontMessageAsync(JsonElement message, CancellationToken cancellationToken = default)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            await WriteErrorAsync(default, -32600, "Invalid Request", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var hasMethod = message.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String;
        var hasId = message.TryGetProperty("id", out var idElement) && idElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        if (!hasMethod)
        {
            if (hasId)
            {
                await HandleClientResponseAsync(message, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteErrorAsync(default, -32600, "Invalid Request", null, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        var method = methodElement.GetString()!;
        var parameters = message.TryGetProperty("params", out var paramsElement) ? paramsElement.Clone() : EmptyObject();
        if (!hasId)
        {
            await HandleFrontNotificationAsync(method, parameters, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _requestConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await HandleFrontRequestAsync(idElement.Clone(), method, parameters, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _requestConcurrency.Release();
        }
    }

    public void RegisterWorker(IAppServerWorker worker)
    {
        if (!_registeredWorkers.TryAdd(worker.WorkerId.Value, worker))
        {
            return;
        }
        worker.NotificationReceived += OnWorkerNotification;
        worker.ServerRequestReceived += OnWorkerServerRequest;
        worker.StateChanged += OnWorkerStateChanged;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _disposeCts.Cancel();
        _workerEvents.Writer.TryComplete();
        foreach (var worker in _registeredWorkers.Values)
        {
            DetachWorker(worker);
        }
        _registeredWorkers.Clear();
        _serverRequests.Clear();
        if (_workerEventLoop is not null)
        {
            try { await _workerEventLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _requestConcurrency.Dispose();
        _writeGate.Dispose();
        await _quotaRefreshProvider.DisposeAsync().ConfigureAwait(false);
        _disposeCts.Dispose();
    }

    private static bool ShouldProcessInline(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return true;
        }
        var hasMethod = message.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String;
        var hasId = message.TryGetProperty("id", out var id) && id.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        if (!hasMethod || !hasId)
        {
            return true;
        }
        return string.Equals(method.GetString(), "initialize", StringComparison.Ordinal);
    }

    private async Task HandleFrontRequestAsync(
        JsonElement id,
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            JsonElement result;
            if (method == "initialize")
            {
                if (Interlocked.CompareExchange(ref _initializedRequestSeen, 1, 0) != 0)
                {
                    throw new AppServerRpcException(-32600, "initialize may only be called once per front connection");
                }
                _workerClientContext.UpdateFromFrontInitialize(parameters);
                result = JsonFromObject(new { userAgent = _options.UserAgent });
            }
            else
            {
                EnsureFrontReady();
                result = method switch
                {
                    "thread/start" => await HandleThreadStartAsync(parameters, cancellationToken).ConfigureAwait(false),
                    "thread/fork" => await HandleThreadForkAsync(parameters, cancellationToken).ConfigureAwait(false),
                    "thread/list" => await HandleThreadListAsync(parameters, cancellationToken).ConfigureAwait(false),
                    _ => await ForwardRoutedRequestAsync(method, parameters, cancellationToken).ConfigureAwait(false)
                };
            }
            await WriteResultAsync(id, result, cancellationToken).ConfigureAwait(false);
        }
        catch (AppServerRpcException ex)
        {
            await WriteErrorAsync(id, ex.Code, ex.Message, ex.ErrorData, cancellationToken).ConfigureAwait(false);
        }
        catch (RoutingDisabledException ex)
        {
            await WriteErrorAsync(id, -32020, ex.Message, null, cancellationToken).ConfigureAwait(false);
        }
        catch (NoEligibleAccountException ex)
        {
            await WriteErrorAsync(id, -32021, ex.Message, JsonFromObject(ex.Candidates), cancellationToken).ConfigureAwait(false);
        }
        catch (PinnedAccountUnavailableException ex)
        {
            await WriteErrorAsync(id, -32022, ex.Message, JsonFromObject(ex.Reasons), cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            await WriteErrorAsync(id, -32023, ex.Message, null, cancellationToken).ConfigureAwait(false);
        }
        catch (ThreadOwnershipCollisionException ex)
        {
            await WriteErrorAsync(id, -32024, ex.Message, JsonFromObject(ex.Accounts.Select(static account => account.Value).ToArray()), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidCompositeCursorException ex)
        {
            await WriteErrorAsync(id, -32602, ex.Message, null, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            await WriteErrorAsync(id, -32025, ex.Message, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(id, -32603, ex.Message, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleFrontNotificationAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (method == "initialized")
        {
            if (Volatile.Read(ref _initializedRequestSeen) == 0)
            {
                return;
            }
            Interlocked.Exchange(ref _initializedNotificationSeen, 1);
            return;
        }

        EnsureFrontReady();
        var accountId = await _projection.GetOrSelectDefaultAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await _workerAccess.AcquireAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        await lease.Worker.SendNotificationAsync(method, parameters, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> HandleThreadStartAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (await IsRouterOffAsync(cancellationToken).ConfigureAwait(false))
        {
            var accountId = await _projection.GetOrSelectDefaultAsync(cancellationToken).ConfigureAwait(false);
            await SetProjectionAsync(accountId, cancellationToken).ConfigureAwait(false);
            await using var passThroughLease = await _workerAccess.AcquireAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
            var passThroughResult = await passThroughLease.Worker.SendRequestAsync(
                "thread/start", parameters, _options.EffectiveRequestTimeout, cancellationToken).ConfigureAwait(false);
            var passThroughThreadId = ExtractThreadIdFromResponse(passThroughResult, "thread/start");
            var existing = await _repository.GetThreadRouteAsync(passThroughThreadId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                await _repository.InsertThreadRouteAsync(new ThreadRoute(
                    passThroughThreadId, accountId, passThroughLease.Worker.WorkerId, RouteReason.Recovery,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            }
            await SetCurrentThreadAsync(passThroughThreadId, cancellationToken).ConfigureAwait(false);
            return passThroughResult;
        }

        var selection = await _router.SelectForNewThreadAsync(
            ExtractRouteRequestContext(parameters),
            cancellationToken).ConfigureAwait(false);
        await SetProjectionAsync(selection.AccountId, cancellationToken).ConfigureAwait(false);
        await using var lease = await _workerAccess.AcquireAccountAsync(selection.AccountId, cancellationToken).ConfigureAwait(false);
        var result = await lease.Worker.SendRequestAsync(
            "thread/start",
            parameters,
            _options.EffectiveRequestTimeout,
            cancellationToken).ConfigureAwait(false);
        var threadId = ExtractThreadIdFromResponse(result, "thread/start");

        try
        {
            await _router.BindNewThreadAsync(threadId, lease.Worker.WorkerId, selection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception firstFailure)
        {
            var existing = await _repository.GetThreadRouteAsync(threadId, CancellationToken.None).ConfigureAwait(false);
            if (existing is null)
            {
                try
                {
                    await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                    await _router.BindNewThreadAsync(threadId, lease.Worker.WorkerId, selection, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception secondFailure)
                {
                    try
                    {
                        await _repository.RecordOrphanThreadAsync(new OrphanThreadRecord(
                            threadId,
                            selection.AccountId,
                            lease.Worker.WorkerId,
                            $"sticky persistence failed: {firstFailure.Message}; retry: {secondFailure.Message}",
                            DateTimeOffset.UtcNow,
                            null), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { }
                throw new InvalidOperationException(
                        $"Codex created thread '{threadId}' but Router could not persist ownership. The thread was recorded for recovery.",
                        secondFailure);
                }
            }
            else if (existing.AccountId != selection.AccountId)
            {
                throw new ThreadOwnershipCollisionException(threadId, new[] { existing.AccountId, selection.AccountId });
            }
        }
        await SetCurrentThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<JsonElement> HandleThreadListAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!await IsRouterOffAsync(cancellationToken).ConfigureAwait(false))
        {
            return await _threadList.ListAsync(parameters, cancellationToken).ConfigureAwait(false);
        }
        var accountId = await _projection.GetOrSelectDefaultAsync(cancellationToken).ConfigureAwait(false);
        await SetProjectionAsync(accountId, cancellationToken).ConfigureAwait(false);
        await using var lease = await _workerAccess.AcquireAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        return await lease.Worker.SendRetryableRequestAsync(
            "thread/list", parameters, DateTimeOffset.UtcNow + _options.EffectiveRequestTimeout,
            retryable: true, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> HandleThreadForkAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var sourceThreadId = ExtractRequiredThreadId(parameters);
        var routerOff = await IsRouterOffAsync(cancellationToken).ConfigureAwait(false);
        ThreadRoute sourceRoute;
        if (routerOff)
        {
            var existing = await _repository.GetThreadRouteAsync(sourceThreadId, cancellationToken).ConfigureAwait(false);
            var accountId = existing?.AccountId ?? await _projection.GetOrSelectDefaultAsync(cancellationToken).ConfigureAwait(false);
            sourceRoute = existing ?? new ThreadRoute(sourceThreadId, accountId, new WorkerId($"passthrough-{accountId.Value}"),
                RouteReason.Recovery, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        }
        else
        {
            sourceRoute = await _workerAccess.ResolveOrDiscoverThreadAsync(sourceThreadId, cancellationToken).ConfigureAwait(false);
        }
        await SetProjectionAsync(sourceRoute.AccountId, cancellationToken).ConfigureAwait(false);
        await using var lease = await _workerAccess.AcquireAccountAsync(sourceRoute.AccountId, cancellationToken).ConfigureAwait(false);
        var result = await lease.Worker.SendRequestAsync(
            "thread/fork",
            parameters,
            _options.EffectiveRequestTimeout,
            cancellationToken).ConfigureAwait(false);
        var forkThreadId = ExtractThreadIdFromResponse(result, "thread/fork");
        try
        {
            if (routerOff)
            {
                var persistedSource = await _repository.GetThreadRouteAsync(sourceThreadId, cancellationToken).ConfigureAwait(false);
                if (persistedSource is null)
                {
                    persistedSource = sourceRoute with { WorkerId = lease.Worker.WorkerId };
                    await _repository.InsertThreadRouteAsync(persistedSource, cancellationToken).ConfigureAwait(false);
                }
                await _repository.InsertThreadRouteAsync(new ThreadRoute(
                    forkThreadId,
                    persistedSource.AccountId,
                    lease.Worker.WorkerId,
                    RouteReason.Recovery,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _router.BindForkAsync(sourceThreadId, forkThreadId, lease.Worker.WorkerId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            try
            {
                await _repository.RecordOrphanThreadAsync(new OrphanThreadRecord(
                    forkThreadId,
                    sourceRoute.AccountId,
                    lease.Worker.WorkerId,
                    $"fork sticky persistence failed: {ex.Message}",
                    DateTimeOffset.UtcNow,
                    null), CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
            throw;
        }
        await SetCurrentThreadAsync(forkThreadId, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<JsonElement> ForwardRoutedRequestAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        AccountId accountId;
        ThreadId? threadId = null;
        if (TryExtractThreadId(parameters, out var extractedThreadId))
        {
            threadId = extractedThreadId;
            if (await IsRouterOffAsync(cancellationToken).ConfigureAwait(false))
            {
                var route = await _repository.GetThreadRouteAsync(extractedThreadId, cancellationToken).ConfigureAwait(false);
                accountId = route?.AccountId ?? await _projection.GetOrSelectDefaultAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var route = await _workerAccess.ResolveOrDiscoverThreadAsync(extractedThreadId, cancellationToken).ConfigureAwait(false);
                accountId = route.AccountId;
            }
            await SetProjectionAsync(accountId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            accountId = await _projection.GetOrSelectDefaultAsync(cancellationToken).ConfigureAwait(false);
            await SetProjectionAsync(accountId, cancellationToken).ConfigureAwait(false);
        }

        await using var lease = await _workerAccess.AcquireAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        var retryable = IsRetryableRead(method);
        JsonElement result;
        if (retryable)
        {
            result = await lease.Worker.SendRetryableRequestAsync(
                method,
                parameters,
                DateTimeOffset.UtcNow + _options.EffectiveRequestTimeout,
                retryable: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await lease.Worker.SendRequestAsync(
                method,
                parameters,
                _options.EffectiveRequestTimeout,
                cancellationToken).ConfigureAwait(false);
        }

        if (threadId is { } routedThread)
        {
            if (await _repository.GetThreadRouteAsync(routedThread, cancellationToken).ConfigureAwait(false) is null &&
                await IsRouterOffAsync(cancellationToken).ConfigureAwait(false))
            {
                await _repository.InsertThreadRouteAsync(new ThreadRoute(
                    routedThread, accountId, lease.Worker.WorkerId, RouteReason.Recovery,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            }
            if (method == "thread/delete")
            {
                await _repository.DeleteThreadRouteAsync(routedThread, cancellationToken).ConfigureAwait(false);
                var current = await _repository.GetRuntimeStateAsync("front_thread_id", cancellationToken)
                    .ConfigureAwait(false);
                if (string.Equals(current?.Value, routedThread.Value, StringComparison.Ordinal))
                {
                    await _repository.DeleteRuntimeStateAsync("front_thread_id", cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                await _router.TouchThreadAsync(routedThread, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (method is "thread/resume" or "turn/start" or "turn/interrupt")
                {
                    await SetCurrentThreadAsync(routedThread, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        return result;
    }

    private async Task HandleClientResponseAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (!message.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
        {
            return;
        }
        var id = idElement.GetString();
        if (id is null || !_serverRequests.TryRemove(id, out var pending))
        {
            return;
        }

        RpcErrorPayload? error = null;
        object? result = null;
        if (message.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object)
        {
            var code = errorElement.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsedCode)
                ? parsedCode
                : -32603;
            var text = errorElement.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString() ?? "client error"
                : "client error";
            JsonElement? data = errorElement.TryGetProperty("data", out var dataElement) ? dataElement.Clone() : null;
            error = new RpcErrorPayload(code, text, data);
        }
        else if (message.TryGetProperty("result", out var resultElement))
        {
            result = resultElement.Clone();
        }

        await pending.Worker.RespondToServerRequestAsync(
            pending.Request,
            result,
            error,
            cancellationToken).ConfigureAwait(false);
    }

    private void OnWorkerNotification(object? sender, WorkerNotification notification) =>
        _workerEvents.Writer.TryWrite(new WorkerEvent.Notification(notification));

    private void OnWorkerServerRequest(object? sender, WorkerServerRequest request)
    {
        if (sender is IAppServerWorker worker)
        {
            _workerEvents.Writer.TryWrite(new WorkerEvent.ServerRequest(worker, request));
        }
    }

    private void OnWorkerStateChanged(object? sender, WorkerStateChange change)
    {
        if (change.Current is not (WorkerState.Stopped or WorkerState.Failed or WorkerState.Crashed or WorkerState.Quarantined) ||
            !_registeredWorkers.TryRemove(change.WorkerId.Value, out var worker))
        {
            return;
        }
        DetachWorker(worker);
        foreach (var pending in _serverRequests.Where(pair => pair.Value.Worker.WorkerId == change.WorkerId).ToArray())
        {
            _serverRequests.TryRemove(pending.Key, out _);
        }
    }

    private async Task ProcessWorkerEventsAsync(CancellationToken cancellationToken)
    {
        await foreach (var workerEvent in _workerEvents.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (workerEvent)
            {
                case WorkerEvent.Notification notification:
                    if (notification.Value.Method.StartsWith("account/", StringComparison.Ordinal) &&
                        _projection.Current is { } projected &&
                        notification.Value.AccountId != projected)
                    {
                        continue;
                    }
                    await WriteNotificationAsync(
                        notification.Value.Method,
                        notification.Value.Parameters,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case WorkerEvent.ServerRequest serverRequest:
                    var frontId = $"router-srv-{Guid.NewGuid():N}";
                    _serverRequests[frontId] = new PendingServerRequest(serverRequest.Worker, serverRequest.Value);
                    await WriteServerRequestAsync(frontId, serverRequest.Value.Method, serverRequest.Value.Parameters, cancellationToken)
                        .ConfigureAwait(false);
                    break;
            }
        }
    }

    private void DetachWorker(IAppServerWorker worker)
    {
        worker.NotificationReceived -= OnWorkerNotification;
        worker.ServerRequestReceived -= OnWorkerServerRequest;
        worker.StateChanged -= OnWorkerStateChanged;
    }

    private async Task PrefetchStaleQuotasAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _repository.GetRouterSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (settings.Mode != RouterMode.Off)
            {
                await _quotaRefreshProvider.RefreshStaleAsync(
                        settings.QuotaStaleAfter,
                        settings.ShortReservePercent,
                        settings.LongReservePercent,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The front connection is shutting down.
        }
        catch
        {
            // Startup prefetch is best effort. A later thread/start invokes the
            // same coalesced refresher and the routing engine rejects stale data
            // if the account still cannot be refreshed.
        }
    }

    private void EnsureFrontReady()
    {
        if (Volatile.Read(ref _initializedRequestSeen) == 0)
        {
            throw new AppServerRpcException(-32002, "initialize must be called first");
        }

        // Codex Desktop's login window sends account/login/* immediately after
        // initialize and often never emits the initialized notification on that
        // connection. Official app-server accepts this; requiring the
        // notification here blocks Desktop login while Overlay (which talks to
        // a fully handshaken official worker) still works.
    }

    private static ThreadId ExtractRequiredThreadId(JsonElement parameters)
    {
        if (!TryExtractThreadId(parameters, out var threadId))
        {
            throw new AppServerRpcException(-32602, "Request params do not contain a valid threadId.");
        }
        return threadId;
    }

    private static RouteRequestContext? ExtractRouteRequestContext(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var model = TryReadString(parameters, "model");
        var modelProvider = TryReadString(parameters, "modelProvider");
        if (parameters.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object)
        {
            model ??= TryReadString(config, "model");
            modelProvider ??= TryReadString(config, "modelProvider");
        }
        return model is null && modelProvider is null
            ? null
            : new RouteRequestContext(model, ModelProvider: modelProvider);
    }

    private static string? TryReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static bool TryExtractThreadId(JsonElement parameters, out ThreadId threadId)
    {
        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("threadId", out var threadIdElement) &&
            threadIdElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(threadIdElement.GetString()))
        {
            threadId = new ThreadId(threadIdElement.GetString()!);
            return true;
        }
        threadId = default;
        return false;
    }

    private static ThreadId ExtractThreadIdFromResponse(JsonElement response, string method)
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
        throw new InvalidDataException($"{method} response does not contain thread.id.");
    }

    private async Task SetCurrentThreadAsync(ThreadId threadId, CancellationToken cancellationToken)
    {
        var persisted = await _repository.GetRuntimeStateAsync("front_thread_id", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(persisted?.Value, threadId.Value, StringComparison.Ordinal))
        {
            await _repository.SetRuntimeStateAsync("front_thread_id", threadId.Value, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SetProjectionAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        _projection.Set(accountId);
        var persisted = await _repository.GetRuntimeStateAsync("front_account_id", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(persisted?.Value, accountId.Value, StringComparison.Ordinal))
        {
            await _repository.SetRuntimeStateAsync("front_account_id", accountId.Value, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> IsRouterOffAsync(CancellationToken cancellationToken) =>
        (await _repository.GetRouterSettingsAsync(cancellationToken).ConfigureAwait(false)).Mode == RouterMode.Off;

    private static bool IsRetryableRead(string method) => method is
        "account/read" or
        "account/rateLimits/read" or
        "account/usage/read" or
        "thread/read";

    private async Task WriteResultAsync(JsonElement id, JsonElement result, CancellationToken cancellationToken) =>
        await WriteEnvelopeAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            writer.WritePropertyName("result");
            result.WriteTo(writer);
            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);

    private async Task WriteErrorAsync(
        JsonElement id,
        int code,
        string message,
        JsonElement? data,
        CancellationToken cancellationToken) =>
        await WriteEnvelopeAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("id");
            if (id.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) writer.WriteNullValue(); else id.WriteTo(writer);
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            if (data is { } errorData)
            {
                writer.WritePropertyName("data");
                errorData.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);

    private async Task WriteNotificationAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken) =>
        await WriteEnvelopeAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            parameters.WriteTo(writer);
            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);

    private async Task WriteServerRequestAsync(
        string id,
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken) =>
        await WriteEnvelopeAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            parameters.WriteTo(writer);
            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);

    private async Task WriteEnvelopeAsync(Action<Utf8JsonWriter> write, CancellationToken cancellationToken)
    {
        var output = _output ?? throw new InvalidOperationException("Front output is not attached. Call RunAsync first.");
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }
        var line = Encoding.UTF8.GetString(buffer.ToArray());
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await output.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static JsonElement JsonFromObject<T>(T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return document.RootElement.Clone();
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private sealed record PendingServerRequest(IAppServerWorker Worker, WorkerServerRequest Request);

    private abstract record WorkerEvent
    {
        public sealed record Notification(WorkerNotification Value) : WorkerEvent;
        public sealed record ServerRequest(IAppServerWorker Worker, WorkerServerRequest Value) : WorkerEvent;
    }
}
