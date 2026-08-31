using System.Text.Json;
using CodexRouter.Domain;
using CodexRouter.Workers;

namespace CodexRouter.Accounts;

public sealed class ChatGptLoginSession : IAsyncDisposable
{
    private readonly WorkerLease _lease;
    private readonly TaskCompletionSource<LoginCompletion> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    internal ChatGptLoginSession(
        WorkerLease lease,
        string loginId,
        Uri authUrl,
        DateTimeOffset startedAt)
    {
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        LoginId = string.IsNullOrWhiteSpace(loginId)
            ? throw new ArgumentException("Login id cannot be empty.", nameof(loginId))
            : loginId;
        AuthUrl = authUrl ?? throw new ArgumentNullException(nameof(authUrl));
        StartedAt = startedAt;
        _lease.Worker.NotificationReceived += OnNotificationReceived;
    }

    public AccountId AccountId => _lease.AccountId;
    public IAppServerWorker Worker => _lease.Worker;
    public string LoginId { get; }
    public Uri AuthUrl { get; }
    public DateTimeOffset StartedAt { get; }
    public bool IsCompleted => _completion.Task.IsCompleted;

    public async Task<LoginCompletion> WaitForCompletionAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        return await _completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        if (_completion.Task.IsCompleted)
        {
            return;
        }

        try
        {
            _ = await Worker.SendRequestAsync(
                "account/login/cancel",
                new { loginId = LoginId },
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _completion.TrySetResult(new LoginCompletion(
                LoginId,
                false,
                "canceled",
                DateTimeOffset.UtcNow));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Worker.NotificationReceived -= OnNotificationReceived;
        if (!_completion.Task.IsCompleted)
        {
            try { await CancelAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }
        await _lease.DisposeAsync().ConfigureAwait(false);
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
        if (loginId is not null && !string.Equals(loginId, LoginId, StringComparison.Ordinal))
        {
            return;
        }

        var success = notification.Parameters.TryGetProperty("success", out var successElement) &&
                      successElement.ValueKind == JsonValueKind.True;
        var error = notification.Parameters.TryGetProperty("error", out var errorElement) &&
                    errorElement.ValueKind == JsonValueKind.String
            ? errorElement.GetString()
            : null;

        _completion.TrySetResult(new LoginCompletion(
            LoginId,
            success,
            error,
            notification.ReceivedAt));
    }
}
