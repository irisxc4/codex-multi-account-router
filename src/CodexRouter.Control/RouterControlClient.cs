using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CodexRouter.Control;

public sealed class RouterControlClient
{
    private readonly ControlEndpoint _endpoint;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private long _requestId;

    public RouterControlClient(string root)
    {
        _endpoint = new ControlEndpoint(root);
    }

    public async Task<bool> IsAvailableAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", _endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout ?? TimeSpan.FromMilliseconds(300));
            await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
            return pipe.IsConnected;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return false;
        }
    }

    public Task<ControlSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<ControlSnapshot>("snapshot", null, cancellationToken);

    public Task<ControlModeChange> SetAutoAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<ControlModeChange>("router/mode", new { mode = "auto" }, cancellationToken);

    public Task<ControlModeChange> SetOffAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<ControlModeChange>("router/mode", new { mode = "off" }, cancellationToken);

    public Task<ControlModeChange> PinAsync(string accountId, CancellationToken cancellationToken = default) =>
        InvokeAsync<ControlModeChange>("router/mode", new { mode = "pinned", accountId }, cancellationToken);

    public Task<ControlLoginStart> StartOnboardAsync(
        string alias,
        string loginMethod = ControlLoginMethods.Browser,
        string? proxyUrl = null,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<ControlLoginStart>("account/onboard/start", new { alias, loginMethod, proxyUrl }, cancellationToken);

    public Task<ChatGptSessionOnboardingResult> ImportChatGptSessionAsync(
        string alias,
        string sessionJson,
        string? proxyUrl = null,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<ChatGptSessionOnboardingResult>(
            "account/session/import",
            new { alias, sessionJson, proxyUrl },
            cancellationToken);

    public Task<ControlLoginStart> StartLoginAsync(string accountId, CancellationToken cancellationToken = default) =>
        InvokeAsync<ControlLoginStart>("account/login/start", new { accountId }, cancellationToken);

    public Task<ControlLoginStatus> LoginStatusAsync(string loginId, CancellationToken cancellationToken = default) =>
        InvokeAsync<ControlLoginStatus>("account/login/status", new { loginId }, cancellationToken);

    public Task<ControlLoginStatus> CancelLoginAsync(string loginId, CancellationToken cancellationToken = default) =>
        InvokeAsync<ControlLoginStatus>("account/login/cancel", new { loginId }, cancellationToken);

    public Task<JsonElement> RefreshQuotaAsync(string accountId, CancellationToken cancellationToken = default) =>
        InvokeAsync<JsonElement>("account/refreshQuota", new { accountId }, cancellationToken);

    public Task<JsonElement> RenameAsync(string accountId, string alias, CancellationToken cancellationToken = default) =>
        InvokeAsync<JsonElement>("account/rename", new { accountId, alias }, cancellationToken);

    public Task<JsonElement> SetEnabledAsync(string accountId, bool enabled, CancellationToken cancellationToken = default) =>
        InvokeAsync<JsonElement>("account/enable", new { accountId, enabled }, cancellationToken);

    public Task<JsonElement> DeleteAsync(string accountId, bool force = false, CancellationToken cancellationToken = default) =>
        InvokeAsync<JsonElement>("account/delete", new { accountId, force }, cancellationToken);

    public Task<ControlMigrationStart> StartMigrationAsync(string sourceThreadId, string targetAccountId, CancellationToken cancellationToken = default) =>
        InvokeAsync<ControlMigrationStart>("migration/start", new { sourceThreadId, targetAccountId }, cancellationToken);

    public Task<ControlMigrationStatus> MigrationStatusAsync(string jobId, CancellationToken cancellationToken = default) =>
        InvokeAsync<ControlMigrationStatus>("migration/status", new { jobId }, cancellationToken);

    public Task<ControlMigrationStart> RetryMigrationAsync(string jobId, CancellationToken cancellationToken = default) =>
        InvokeAsync<ControlMigrationStart>("migration/retry", new { jobId }, cancellationToken);

    public Task<ControlMigrationStatus> CancelMigrationAsync(string jobId, CancellationToken cancellationToken = default) =>
        InvokeAsync<ControlMigrationStatus>("migration/cancel", new { jobId }, cancellationToken);

    public async Task<T> InvokeAsync<T>(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("Control method is required.", nameof(method));
        var token = await _endpoint.ReadTokenAsync(cancellationToken).ConfigureAwait(false);
        await using var pipe = new NamedPipeClientStream(
            ".", _endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(TimeSpan.FromSeconds(3));
        await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);

        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 16 * 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        var id = Interlocked.Increment(ref _requestId);
        var request = JsonSerializer.Serialize(new
        {
            id,
            token,
            method,
            @params = parameters ?? new { }
        }, _json);
        await writer.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);

        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new IOException("Codex Router control pipe closed without a response.");
        }
        using var response = JsonDocument.Parse(line);
        if (response.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsed) ? parsed : 500;
            var message = error.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString() ?? "control error"
                : "control error";
            throw new RouterControlException(code, message);
        }
        if (!response.RootElement.TryGetProperty("result", out var result))
        {
            throw new InvalidDataException("Codex Router control response has no result.");
        }
        var value = result.Deserialize<T>(_json);
        return value ?? throw new InvalidDataException($"Control result for '{method}' could not be deserialized.");
    }
}

public sealed class RouterControlException : Exception
{
    public RouterControlException(int code, string message) : base(message) => Code = code;
    public int Code { get; }
}
