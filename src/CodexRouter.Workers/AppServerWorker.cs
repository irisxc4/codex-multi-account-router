using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodexRouter.Domain;

namespace CodexRouter.Workers;

public sealed record WorkerLaunchSpec(
    WorkerId WorkerId,
    AccountId AccountId,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string CodexHome,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? ExtraEnvironment = null)
{
    public static WorkerLaunchSpec ForCodex(
        WorkerId workerId,
        AccountId accountId,
        string codexExecutable,
        string codexHome) =>
        new(workerId, accountId, codexExecutable, new[] { "app-server" }, codexHome);
}

public sealed record WorkerStartOptions(
    TimeSpan? InitializeTimeout = null,
    TimeSpan? StopTimeout = null,
    string ClientName = "codex-router",
    string ClientTitle = "Codex Router",
    string ClientVersion = "0.1.0",
    bool ExperimentalApi = false)
{
    public TimeSpan EffectiveInitializeTimeout => InitializeTimeout ?? TimeSpan.FromSeconds(15);
    public TimeSpan EffectiveStopTimeout => StopTimeout ?? TimeSpan.FromSeconds(5);
}

public sealed record WorkerNotification(
    WorkerId WorkerId,
    AccountId AccountId,
    string Method,
    JsonElement Parameters,
    DateTimeOffset ReceivedAt);

public sealed record WorkerServerRequest(
    WorkerId WorkerId,
    AccountId AccountId,
    JsonElement Id,
    string Method,
    JsonElement Parameters,
    DateTimeOffset ReceivedAt);

public sealed record WorkerStateChange(
    WorkerId WorkerId,
    AccountId AccountId,
    WorkerState Previous,
    WorkerState Current,
    string? Reason,
    DateTimeOffset ChangedAt);

public sealed record RpcErrorPayload(int Code, string Message, JsonElement? Data = null);

public sealed class WorkerProtocolException : Exception
{
    public WorkerProtocolException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class WorkerExitedException : Exception
{
    public WorkerExitedException(string message, int? exitCode = null) : base(message) => ExitCode = exitCode;
    public int? ExitCode { get; }
}

public sealed class AppServerRpcException : Exception
{
    public AppServerRpcException(int code, string message, JsonElement? data = null)
        : base(message)
    {
        Code = code;
        ErrorData = data;
    }

    public int Code { get; }
    public JsonElement? ErrorData { get; }
    public bool IsServerOverloaded => Code == -32001;
}

public sealed record RetryPolicy(
    int MaxAttempts = 5,
    TimeSpan? InitialDelay = null,
    TimeSpan? MaxDelay = null,
    double JitterRatio = 0.20)
{
    public TimeSpan EffectiveInitialDelay => InitialDelay ?? TimeSpan.FromMilliseconds(200);
    public TimeSpan EffectiveMaxDelay => MaxDelay ?? TimeSpan.FromSeconds(5);
}

public interface IRetryScheduler
{
    DateTimeOffset UtcNow { get; }
    double NextUnitDouble();
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemRetryScheduler : IRetryScheduler
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public double NextUnitDouble() => Random.Shared.NextDouble();
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public interface IAppServerWorker : IAsyncDisposable
{
    WorkerId WorkerId { get; }
    AccountId AccountId { get; }
    WorkerState State { get; }
    int? ProcessId { get; }
    bool IsAlive { get; }
    event EventHandler<WorkerStateChange>? StateChanged;
    event EventHandler<WorkerNotification>? NotificationReceived;
    event EventHandler<WorkerServerRequest>? ServerRequestReceived;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> SendRequestAsync(string method, object? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
    Task<JsonElement> SendRetryableRequestAsync(string method, object? parameters, DateTimeOffset deadline, bool retryable, RetryPolicy? policy = null, CancellationToken cancellationToken = default);
    Task SendNotificationAsync(string method, object? parameters = null, CancellationToken cancellationToken = default);
    Task RespondToServerRequestAsync(WorkerServerRequest request, object? result = null, RpcErrorPayload? error = null, CancellationToken cancellationToken = default);
    IAsyncEnumerable<WorkerNotification> ReadNotificationsAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<WorkerServerRequest> ReadServerRequestsAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<string> GetRecentStderr();
}

public sealed class AppServerWorker : IAppServerWorker
{
    private const int StderrLineLimit = 512;
    private readonly WorkerLaunchSpec _launch;
    private readonly WorkerStartOptions _options;
    private readonly IRetryScheduler _retryScheduler;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<WorkerNotification> _notifications = Channel.CreateUnbounded<WorkerNotification>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true, AllowSynchronousContinuations = false });
    private readonly Channel<WorkerServerRequest> _serverRequests = Channel.CreateUnbounded<WorkerServerRequest>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true, AllowSynchronousContinuations = false });
    private readonly ConcurrentQueue<string> _stderr = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private long _requestId;
    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _stdoutLoop;
    private Task? _stderrLoop;
    private CancellationTokenSource? _lifetimeCts;
    private WorkerState _state = WorkerState.Stopped;
    private bool _disposeRequested;

    public AppServerWorker(
        WorkerLaunchSpec launch,
        WorkerStartOptions? options = null,
        IRetryScheduler? retryScheduler = null)
    {
        _launch = launch ?? throw new ArgumentNullException(nameof(launch));
        _options = options ?? new WorkerStartOptions();
        _retryScheduler = retryScheduler ?? new SystemRetryScheduler();

        if (string.IsNullOrWhiteSpace(_launch.ExecutablePath))
        {
            throw new ArgumentException("Worker executable path is required.", nameof(launch));
        }
        if (string.IsNullOrWhiteSpace(_launch.CodexHome))
        {
            throw new ArgumentException("Worker CODEX_HOME is required.", nameof(launch));
        }
    }

    public WorkerId WorkerId => _launch.WorkerId;
    public AccountId AccountId => _launch.AccountId;
    public WorkerState State { get { lock (_stateGate) return _state; } }
    public int? ProcessId { get { try { return _process is { HasExited: false } process ? process.Id : null; } catch (InvalidOperationException) { return null; } } }
    public bool IsAlive { get { try { return _process is { HasExited: false }; } catch (InvalidOperationException) { return false; } } }
    public event EventHandler<WorkerStateChange>? StateChanged;
    public event EventHandler<WorkerNotification>? NotificationReceived;
    public event EventHandler<WorkerServerRequest>? ServerRequestReceived;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeRequested, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is not (WorkerState.Stopped or WorkerState.Crashed or WorkerState.Failed or WorkerState.Backoff))
            {
                if (State is WorkerState.Ready or WorkerState.Busy or WorkerState.Draining)
                {
                    return;
                }
                throw new InvalidOperationException($"Worker cannot start from state {State}.");
            }

            await CleanupPreviousProcessAsync().ConfigureAwait(false);
            SetState(WorkerState.Starting, null);

            var startInfo = new ProcessStartInfo
            {
                FileName = _launch.ExecutablePath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _launch.WorkingDirectory ?? Environment.CurrentDirectory,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            foreach (var argument in _launch.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            startInfo.Environment["CODEX_HOME"] = Path.GetFullPath(_launch.CodexHome);
            if (_launch.ExtraEnvironment is not null)
            {
                foreach (var pair in _launch.ExtraEnvironment)
                {
                    if (pair.Value is null)
                    {
                        startInfo.Environment.Remove(pair.Key);
                    }
                    else
                    {
                        startInfo.Environment[pair.Key] = pair.Value;
                    }
                }
            }
            // CODEX_CLI_PATH is a Codex Desktop launcher override. A real worker must
            // never inherit the Router shim through this variable, even if the parent
            // Desktop/shim process has it set.
            startInfo.Environment.Remove("CODEX_CLI_PATH");

            Directory.CreateDirectory(_launch.CodexHome);
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("Process.Start returned false.");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                process.Dispose();
                SetState(WorkerState.Failed, ex.Message);
                throw new WorkerExitedException($"Could not start worker executable '{_launch.ExecutablePath}'.");
            }

            _process = process;
            _stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false), 16 * 1024, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            _lifetimeCts = new CancellationTokenSource();
            _stdoutLoop = Task.Run(() => StdoutLoopAsync(process, _lifetimeCts.Token), CancellationToken.None);
            _stderrLoop = Task.Run(() => StderrLoopAsync(process, _lifetimeCts.Token), CancellationToken.None);

            SetState(WorkerState.Initializing, null);
            try
            {
                var initializeParams = new
                {
                    clientInfo = new
                    {
                        name = _options.ClientName,
                        title = _options.ClientTitle,
                        version = _options.ClientVersion
                    },
                    capabilities = new
                    {
                        experimentalApi = _options.ExperimentalApi
                    }
                };
                _ = await SendRequestAsync("initialize", initializeParams, _options.EffectiveInitializeTimeout, cancellationToken)
                    .ConfigureAwait(false);
                await SendNotificationAsync("initialized", null, cancellationToken).ConfigureAwait(false);
                if (!IsAlive)
                {
                    throw new WorkerExitedException("Worker exited immediately after initialize.", SafeExitCode(process));
                }
                SetState(WorkerState.Ready, null);
            }
            catch
            {
                // This is an initialization failure owned by the caller, not an unexpected
                // runtime crash. Move to Stopping before terminating the child so the
                // stdout loop cannot race the cleanup and misclassify it as Crashed.
                SetState(WorkerState.Stopping, "initialize failed");
                await StopProcessAfterStartFailureAsync(process).ConfigureAwait(false);
                SetState(WorkerState.Failed, "initialize failed");
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<JsonElement> SendRequestAsync(
        string method,
        object? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("RPC method is required.", nameof(method));
        }
        EnsureCanSend();

        var id = Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException($"Duplicate worker request id {id}.");
        }

        try
        {
            await WriteMessageAsync(new RpcOutboundRequest(id, method, parameters), cancellationToken).ConfigureAwait(false);
            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
            if (effectiveTimeout <= TimeSpan.Zero)
            {
                throw new TimeoutException($"Request '{method}' deadline was already expired.");
            }
            return await completion.Task.WaitAsync(effectiveTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Worker request '{method}' timed out after {(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds:0} ms.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task<JsonElement> SendRetryableRequestAsync(
        string method,
        object? parameters,
        DateTimeOffset deadline,
        bool retryable,
        RetryPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        policy ??= new RetryPolicy();
        if (policy.MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "MaxAttempts must be at least one.");
        }
        if (policy.JitterRatio is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "JitterRatio must be between zero and one.");
        }

        AppServerRpcException? lastOverload = null;
        for (var attempt = 1; attempt <= policy.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadline - _retryScheduler.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException($"Retry deadline expired before '{method}' could complete.", lastOverload);
            }

            try
            {
                return await SendRequestAsync(method, parameters, remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (AppServerRpcException ex) when (retryable && ex.IsServerOverloaded && attempt < policy.MaxAttempts)
            {
                lastOverload = ex;
                var delay = ComputeRetryDelay(policy, attempt);
                remaining = deadline - _retryScheduler.UtcNow;
                if (remaining <= delay)
                {
                    throw new TimeoutException($"Retry deadline would expire before retrying '{method}'.", ex);
                }
                await _retryScheduler.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw (Exception?)lastOverload ?? new InvalidOperationException($"Retry loop for '{method}' ended without a result.");
    }

    public Task SendNotificationAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("Notification method is required.", nameof(method));
        }
        EnsureCanSend();
        return WriteMessageAsync(new RpcOutboundNotification(method, parameters), cancellationToken);
    }

    public Task RespondToServerRequestAsync(
        WorkerServerRequest request,
        object? result = null,
        RpcErrorPayload? error = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.WorkerId != WorkerId || request.AccountId != AccountId)
        {
            throw new InvalidOperationException("Cannot respond to a server request owned by another worker.");
        }
        if (error is not null && result is not null)
        {
            throw new ArgumentException("A server-request response cannot contain both result and error.");
        }
        EnsureCanSend();
        return WriteRawResponseAsync(request.Id, result, error, cancellationToken);
    }

    public async IAsyncEnumerable<WorkerNotification> ReadNotificationsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _notifications.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_notifications.Reader.TryRead(out var item))
            {
                yield return item;
            }
        }
    }

    public async IAsyncEnumerable<WorkerServerRequest> ReadServerRequestsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _serverRequests.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_serverRequests.Reader.TryRead(out var item))
            {
                yield return item;
            }
        }
    }

    public IReadOnlyList<string> GetRecentStderr() => _stderr.ToArray();

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var process = _process;
            if (process is null)
            {
                SetState(WorkerState.Stopped, null);
                return;
            }
            if (State == WorkerState.Stopped)
            {
                return;
            }

            SetState(WorkerState.Stopping, null);
            try
            {
                if (_stdin is not null)
                {
                    await _stdin.FlushAsync().ConfigureAwait(false);
                    await _stdin.DisposeAsync().ConfigureAwait(false);
                    _stdin = null;
                }
            }
            catch (IOException)
            {
                // Child may already have closed the pipe.
            }

            if (!SafeHasExited(process))
            {
                using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                stopCts.CancelAfter(_options.EffectiveStopTimeout);
                try
                {
                    await process.WaitForExitAsync(stopCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            _lifetimeCts?.Cancel();
            await AwaitLoopQuietlyAsync(_stdoutLoop).ConfigureAwait(false);
            await AwaitLoopQuietlyAsync(_stderrLoop).ConfigureAwait(false);
            FailAllPending(new WorkerExitedException("Worker stopped.", SafeExitCode(process)));
            SetState(WorkerState.Stopped, null);
            CleanupProcessObjects();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposeRequested)
        {
            return;
        }
        _disposeRequested = true;
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            if (_process is not null)
            {
                TryKill(_process);
            }
            CleanupProcessObjects();
        }
        _notifications.Writer.TryComplete();
        _serverRequests.Writer.TryComplete();
        _writeGate.Dispose();
        _lifecycleGate.Dispose();
    }

    private async Task StdoutLoopAsync(Process process, CancellationToken cancellationToken)
    {
        Exception? fault = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                await HandleIncomingLineAsync(line, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            fault = ex;
        }

        if (State is not (WorkerState.Stopping or WorkerState.Stopped) && !_disposeRequested)
        {
            if (!SafeHasExited(process))
            {
                TryKill(process);
            }
            var exitCode = SafeExitCode(process);
            var exception = (Exception?)(fault as WorkerProtocolException)
                ?? new WorkerExitedException(fault is null ? "Worker stdout closed unexpectedly." : $"Worker stdout loop failed: {fault.Message}", exitCode);
            FailAllPending(exception);
            SetState(WorkerState.Crashed, exception.Message);
        }
    }

    private async Task StderrLoopAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
                _stderr.Enqueue(line);
                while (_stderr.Count > StderrLineLimit && _stderr.TryDequeue(out _)) { }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (IOException)
        {
            // Process exit can close stderr abruptly.
        }
    }

    private async Task HandleIncomingLineAsync(string line, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new WorkerProtocolException($"Worker emitted malformed JSONL: {Truncate(line, 300)}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new WorkerProtocolException("Worker emitted a JSONL value that is not an object.");
            }

            var hasMethod = root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String;
            var hasId = root.TryGetProperty("id", out var idElement) && idElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
            if (hasMethod)
            {
                var method = methodElement.GetString()!;
                var parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement.Clone() : EmptyObject();
                if (hasId)
                {
                    var request = new WorkerServerRequest(
                        WorkerId, AccountId, idElement.Clone(), method, parameters, DateTimeOffset.UtcNow);
                    RaiseSafely(ServerRequestReceived, request);
                    await _serverRequests.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var notification = new WorkerNotification(
                        WorkerId, AccountId, method, parameters, DateTimeOffset.UtcNow);
                    RaiseSafely(NotificationReceived, notification);
                    await _notifications.Writer.WriteAsync(notification, cancellationToken).ConfigureAwait(false);
                }
                return;
            }

            if (!hasId || idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt64(out var id))
            {
                throw new WorkerProtocolException("Worker response did not contain a numeric Router-owned request id.");
            }
            if (!_pending.TryGetValue(id, out var completion))
            {
                return; // Late response after timeout/cancellation.
            }

            if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object)
            {
                var code = errorElement.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsedCode)
                    ? parsedCode
                    : -32603;
                var message = errorElement.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString() ?? "AppServer RPC error"
                    : "AppServer RPC error";
                JsonElement? data = errorElement.TryGetProperty("data", out var dataElement) ? dataElement.Clone() : null;
                completion.TrySetException(new AppServerRpcException(code, message, data));
                return;
            }

            var result = root.TryGetProperty("result", out var resultElement) ? resultElement.Clone() : EmptyObject();
            completion.TrySetResult(result);
        }
    }

    private async Task WriteMessageAsync<T>(T message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, _jsonOptions);
        await WriteLineAsync(json, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteRawResponseAsync(JsonElement id, object? result, RpcErrorPayload? error, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            if (error is null)
            {
                writer.WritePropertyName("result");
                JsonSerializer.Serialize(writer, result, _jsonOptions);
            }
            else
            {
                writer.WritePropertyName("error");
                writer.WriteStartObject();
                writer.WriteNumber("code", error.Code);
                writer.WriteString("message", error.Message);
                if (error.Data is { } data)
                {
                    writer.WritePropertyName("data");
                    data.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        await WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray()), cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteLineAsync(string json, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stdin = _stdin ?? throw new WorkerExitedException("Worker stdin is not available.");
            await stdin.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new WorkerExitedException($"Worker stdin write failed: {ex.Message}", SafeExitCode(_process));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private TimeSpan ComputeRetryDelay(RetryPolicy policy, int attempt)
    {
        var baseMs = policy.EffectiveInitialDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1));
        var cappedMs = Math.Min(baseMs, policy.EffectiveMaxDelay.TotalMilliseconds);
        var jitter = (2 * _retryScheduler.NextUnitDouble() - 1) * policy.JitterRatio;
        return TimeSpan.FromMilliseconds(Math.Max(0, cappedMs * (1 + jitter)));
    }

    private void EnsureCanSend()
    {
        if (_stdin is null || !IsAlive || State is WorkerState.Stopped or WorkerState.Stopping or WorkerState.Crashed or WorkerState.Failed or WorkerState.Quarantined)
        {
            throw new WorkerExitedException($"Worker {WorkerId} is not available for RPC in state {State}.", SafeExitCode(_process));
        }
    }

    private void FailAllPending(Exception exception)
    {
        foreach (var pending in _pending)
        {
            pending.Value.TrySetException(exception);
        }
        _pending.Clear();
    }

    private void RaiseSafely<T>(EventHandler<T>? handlers, T value)
    {
        if (handlers is null)
        {
            return;
        }
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
        {
            try { handler(this, value); } catch { /* Observers must not break the protocol read loop. */ }
        }
    }

    private void SetState(WorkerState next, string? reason)
    {
        WorkerState previous;
        lock (_stateGate)
        {
            previous = _state;
            if (previous == next)
            {
                return;
            }
            _state = next;
        }
        StateChanged?.Invoke(this, new WorkerStateChange(WorkerId, AccountId, previous, next, reason, DateTimeOffset.UtcNow));
    }

    private async Task StopProcessAfterStartFailureAsync(Process process)
    {
        TryKill(process);
        try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch (InvalidOperationException) { }
        _lifetimeCts?.Cancel();
        await AwaitLoopQuietlyAsync(_stdoutLoop).ConfigureAwait(false);
        await AwaitLoopQuietlyAsync(_stderrLoop).ConfigureAwait(false);
        CleanupProcessObjects();
    }

    private async Task CleanupPreviousProcessAsync()
    {
        if (_process is null)
        {
            return;
        }
        if (!SafeHasExited(_process))
        {
            TryKill(_process);
            try { await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch (InvalidOperationException) { }
        }
        _lifetimeCts?.Cancel();
        await AwaitLoopQuietlyAsync(_stdoutLoop).ConfigureAwait(false);
        await AwaitLoopQuietlyAsync(_stderrLoop).ConfigureAwait(false);
        CleanupProcessObjects();
    }

    private void CleanupProcessObjects()
    {
        try { _stdin?.Dispose(); } catch { }
        _stdin = null;
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
        _process?.Dispose();
        _process = null;
        _stdoutLoop = null;
        _stderrLoop = null;
    }

    private static async Task AwaitLoopQuietlyAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }
        try { await task.ConfigureAwait(false); } catch { }
    }

    private static bool SafeHasExited(Process process)
    {
        try { return process.HasExited; } catch (InvalidOperationException) { return true; }
    }

    private static int? SafeExitCode(Process? process)
    {
        if (process is null)
        {
            return null;
        }
        try { return process.HasExited ? process.ExitCode : null; } catch (InvalidOperationException) { return null; }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private sealed record RpcOutboundRequest(long Id, string Method, object? Params);
    private sealed record RpcOutboundNotification(string Method, object? Params);
}
