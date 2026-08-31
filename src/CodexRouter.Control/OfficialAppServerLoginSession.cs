using System.Text.Json;
using CodexRouter.Domain;
using CodexRouter.Workers;

namespace CodexRouter.Control;

public interface IOfficialAppServerLoginSession : IAsyncDisposable
{
    AccountId AccountId { get; }
    string LoginId { get; }
    Uri AuthUrl { get; }
    string? UserCode { get; }
    DateTimeOffset StartedAt { get; }
    Task<OfficialAppServerLoginCompletion> WaitForCompletionAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
    Task CancelAsync(CancellationToken cancellationToken = default);
}

public interface IOfficialAppServerLoginSessionFactory
{
    Task<IOfficialAppServerLoginSession> StartAsync(
        string codexExecutable,
        AccountProfile profile,
        string? proxyUrl,
        bool deviceCode = false,
        CancellationToken cancellationToken = default);
}

public sealed class OfficialAppServerLoginSessionFactory : IOfficialAppServerLoginSessionFactory
{
    public async Task<IOfficialAppServerLoginSession> StartAsync(
        string codexExecutable,
        AccountProfile profile,
        string? proxyUrl,
        bool deviceCode = false,
        CancellationToken cancellationToken = default) =>
        await OfficialAppServerLoginSession.StartAsync(
            codexExecutable,
            profile,
            proxyUrl,
            deviceCode,
            cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// Owns one isolated official Codex app-server only for ChatGPT onboarding.
/// The official app-server generates the browser URL or device code, performs token exchange/polling,
/// and persists credentials. Router keeps only allowlisted login metadata and never reads auth tokens.
/// </summary>
public sealed class OfficialAppServerLoginSession : IOfficialAppServerLoginSession
{
    private readonly AppServerWorker _worker;
    private readonly TaskCompletionSource<OfficialAppServerLoginCompletion> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private OfficialAppServerLoginSession(AppServerWorker worker, AccountId accountId)
    {
        _worker = worker;
        AccountId = accountId;
        _worker.NotificationReceived += OnNotificationReceived;
    }

    public AccountId AccountId { get; }
    public string LoginId { get; private set; } = string.Empty;
    public Uri AuthUrl { get; private set; } = null!;
    public string? UserCode { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }

    public static async Task<OfficialAppServerLoginSession> StartAsync(
        string codexExecutable,
        AccountProfile profile,
        string? proxyUrl,
        bool deviceCode = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(codexExecutable) || !File.Exists(codexExecutable))
        {
            throw new FileNotFoundException("Official Codex CLI executable was not found.", codexExecutable);
        }

        var workerId = new WorkerId($"login-{profile.Id.Value}-{Guid.NewGuid():N}");
        var launch = WorkerLaunchSpec.ForCodex(workerId, profile.Id, Path.GetFullPath(codexExecutable), profile.CodexHome)
            with { ExtraEnvironment = CodexLoginProxy.CreateEnvironment(proxyUrl) };
        var worker = new AppServerWorker(
            launch,
            new WorkerStartOptions(
                InitializeTimeout: TimeSpan.FromSeconds(20),
                StopTimeout: TimeSpan.FromSeconds(5),
                ClientName: "codex-router-login",
                ClientTitle: "Codex Router Login",
                ClientVersion: "0.1.0"));
        var session = new OfficialAppServerLoginSession(worker, profile.Id);
        try
        {
            await worker.StartAsync(cancellationToken).ConfigureAwait(false);
            var loginParams = deviceCode
                ? (object)new { type = "chatgptDeviceCode" }
                : new
                {
                    type = "chatgpt",
                    useHostedLoginSuccessPage = true,
                    appBrand = "codex"
                };
            var response = await worker.SendRequestAsync(
                "account/login/start",
                loginParams,
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);

            if (response.ValueKind != JsonValueKind.Object ||
                !response.TryGetProperty("type", out var typeElement) ||
                !response.TryGetProperty("loginId", out var loginIdElement) ||
                loginIdElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("Official Codex returned an invalid ChatGPT login-start response.");
            }

            var expectedType = deviceCode ? "chatgptDeviceCode" : "chatgpt";
            if (!string.Equals(typeElement.GetString(), expectedType, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Official Codex returned unexpected login type '{typeElement.GetString()}'.");
            }

            var loginId = loginIdElement.GetString();
            string? authUrlText;
            string? userCode = null;
            if (deviceCode)
            {
                authUrlText = response.TryGetProperty("verificationUrl", out var verificationUrlElement) &&
                              verificationUrlElement.ValueKind == JsonValueKind.String
                    ? verificationUrlElement.GetString()
                    : null;
                userCode = response.TryGetProperty("userCode", out var userCodeElement) &&
                           userCodeElement.ValueKind == JsonValueKind.String
                    ? userCodeElement.GetString()
                    : null;
            }
            else
            {
                authUrlText = response.TryGetProperty("authUrl", out var authUrlElement) &&
                              authUrlElement.ValueKind == JsonValueKind.String
                    ? authUrlElement.GetString()
                    : null;
            }

            if (string.IsNullOrWhiteSpace(loginId) ||
                !Uri.TryCreate(authUrlText, UriKind.Absolute, out var authUrl) ||
                !IsAllowedOfficialLoginUrl(authUrl) ||
                (deviceCode && string.IsNullOrWhiteSpace(userCode)))
            {
                throw new InvalidOperationException("Official Codex returned an unusable ChatGPT login URL, device code, or login id.");
            }

            session.LoginId = loginId;
            session.AuthUrl = authUrl;
            session.UserCode = userCode;
            session.StartedAt = DateTimeOffset.UtcNow;
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<OfficialAppServerLoginCompletion> WaitForCompletionAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        return _completion.Task.WaitAsync(timeout, cancellationToken);
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        if (_completion.Task.IsCompleted) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(LoginId) && _worker.IsAlive)
            {
                _ = await _worker.SendRequestAsync(
                    "account/login/cancel",
                    new { loginId = LoginId },
                    TimeSpan.FromSeconds(10),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _completion.TrySetResult(new OfficialAppServerLoginCompletion(false, "canceled", DateTimeOffset.UtcNow));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _worker.NotificationReceived -= OnNotificationReceived;
        if (!_completion.Task.IsCompleted)
        {
            try { await CancelAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }
        await _worker.DisposeAsync().ConfigureAwait(false);
    }

    private static bool IsAllowedOfficialLoginUrl(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(uri.Host, "auth.openai.com", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase);
    }

    private void OnNotificationReceived(object? sender, WorkerNotification notification)
    {
        if (!string.Equals(notification.Method, "account/login/completed", StringComparison.Ordinal) ||
            notification.AccountId != AccountId ||
            notification.Parameters.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var loginId = notification.Parameters.TryGetProperty("loginId", out var loginIdElement) &&
                      loginIdElement.ValueKind == JsonValueKind.String
            ? loginIdElement.GetString()
            : null;
        if (!string.IsNullOrWhiteSpace(loginId) && !string.Equals(loginId, LoginId, StringComparison.Ordinal))
        {
            return;
        }

        var success = notification.Parameters.TryGetProperty("success", out var successElement) &&
                      successElement.ValueKind == JsonValueKind.True;
        var error = notification.Parameters.TryGetProperty("error", out var errorElement) &&
                    errorElement.ValueKind == JsonValueKind.String
            ? errorElement.GetString()
            : null;
        _completion.TrySetResult(new OfficialAppServerLoginCompletion(success, error, notification.ReceivedAt));
    }
}

public sealed record OfficialAppServerLoginCompletion(bool Success, string? Error, DateTimeOffset CompletedAt);
