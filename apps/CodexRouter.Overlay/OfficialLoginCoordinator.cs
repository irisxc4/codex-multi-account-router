using CodexRouter.Control;

namespace CodexRouter.Overlay;

public interface IOfficialLoginClient
{
    Task<ControlLoginStart> StartOnboardAsync(
        string alias,
        string loginMethod,
        string? proxyUrl,
        CancellationToken cancellationToken = default);

    Task<ControlLoginStatus> GetLoginStatusAsync(
        string loginId,
        CancellationToken cancellationToken = default);

    Task<ControlLoginStatus> CancelLoginAsync(
        string loginId,
        CancellationToken cancellationToken = default);

    Task<IAsyncDisposable> OpenLoginUrlAsync(
        string url,
        string? proxyUrl,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates the supported OpenAI Codex app-server login sequence without ever
/// receiving auth tokens: start, open the exact official URL, poll, and cancel.
/// </summary>
public sealed class OfficialLoginCoordinator : IAsyncDisposable
{
    private readonly IOfficialLoginClient _client;
    private readonly TimeSpan _pollInterval;
    private IAsyncDisposable? _browser;
    private string? _loginId;
    private ControlLoginStatus? _lastStatus;
    private bool _terminal;
    private int _disposed;

    public OfficialLoginCoordinator(
        IOfficialLoginClient client,
        TimeSpan? pollInterval = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        if (_pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public async Task<ControlLoginStart> StartAsync(
        string alias,
        string loginMethod,
        string? proxyUrl,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_loginId is not null) throw new InvalidOperationException("An official login is already active.");
        if (string.IsNullOrWhiteSpace(alias)) throw new ArgumentException("Account alias cannot be empty.", nameof(alias));
        if (loginMethod is not (ControlLoginMethods.Browser or ControlLoginMethods.Device))
        {
            throw new ArgumentException("Official interactive login must use browser or device code.", nameof(loginMethod));
        }

        var start = await _client.StartOnboardAsync(
            alias.Trim(),
            loginMethod,
            proxyUrl,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(start.LoginId))
        {
            throw new InvalidOperationException("Official Codex returned an empty login id.");
        }
        _loginId = start.LoginId;

        try
        {
            if (!string.Equals(start.LoginMethod, loginMethod, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Official Codex returned a different login method than requested.");
            }
            if (string.IsNullOrWhiteSpace(start.AuthUrl))
            {
                throw new InvalidOperationException("Official Codex did not return a login URL.");
            }
            if (loginMethod == ControlLoginMethods.Device && string.IsNullOrWhiteSpace(start.UserCode))
            {
                throw new InvalidOperationException("Official Codex did not return a device code.");
            }

            _browser = await _client.OpenLoginUrlAsync(
                start.AuthUrl,
                proxyUrl,
                cancellationToken).ConfigureAwait(false);
            return start;
        }
        catch
        {
            await TryCancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ControlLoginStatus> WaitForCompletionAsync(
        IProgress<ControlLoginStatus>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_loginId is null) throw new InvalidOperationException("Official login has not been started.");

        while (true)
        {
            var status = await _client.GetLoginStatusAsync(_loginId, cancellationToken).ConfigureAwait(false);
            _lastStatus = status;
            progress?.Report(status);
            if (!string.Equals(status.State, "pending", StringComparison.OrdinalIgnoreCase))
            {
                _terminal = true;
                return status;
            }
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ControlLoginStatus?> CancelAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_loginId is null || _terminal) return _lastStatus;

        var status = await _client.CancelLoginAsync(_loginId, cancellationToken).ConfigureAwait(false);
        _lastStatus = status;
        _terminal = true;
        return status;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await TryCancelAsync().ConfigureAwait(false);
        if (_browser is not null)
        {
            try { await _browser.DisposeAsync().ConfigureAwait(false); } catch { }
            _browser = null;
        }
    }

    private async Task TryCancelAsync()
    {
        if (_loginId is null || _terminal) return;
        try
        {
            _lastStatus = await _client.CancelLoginAsync(_loginId, CancellationToken.None).ConfigureAwait(false);
            _terminal = true;
        }
        catch
        {
            // Best effort during startup rollback/disposal; the server also owns timeout cleanup.
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(OfficialLoginCoordinator));
    }
}
