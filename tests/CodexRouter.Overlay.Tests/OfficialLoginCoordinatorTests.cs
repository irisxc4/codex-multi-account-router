using CodexRouter.Control;
using CodexRouter.Overlay;
using Xunit;

namespace CodexRouter.Overlay.Tests;

public sealed class OfficialLoginCoordinatorTests
{
    [Fact]
    public async Task Browser_login_uses_official_onboard_and_opens_server_url_unchanged()
    {
        const string authUrl = "https://auth.openai.com/oauth/authorize?client_id=official&state=state-123&code_challenge=challenge";
        var client = new FakeOfficialLoginClient
        {
            StartResult = new ControlLoginStart(
                "account-1",
                "login-1",
                authUrl,
                DateTimeOffset.UtcNow,
                ControlLoginMethods.Browser)
        };
        await using var coordinator = new OfficialLoginCoordinator(client, TimeSpan.FromMilliseconds(1));

        var started = await coordinator.StartAsync("ChatGPT", ControlLoginMethods.Browser, "http://127.0.0.1:7897");

        Assert.Equal(ControlLoginMethods.Browser, client.SeenMethod);
        Assert.Equal("http://127.0.0.1:7897", client.SeenProxyUrl);
        Assert.Equal(authUrl, client.OpenedUrl);
        Assert.Equal(authUrl, started.AuthUrl);
    }

    [Fact]
    public async Task Device_login_requires_the_official_one_time_code()
    {
        var client = new FakeOfficialLoginClient
        {
            StartResult = new ControlLoginStart(
                "account-1",
                "login-1",
                "https://chatgpt.com/device",
                DateTimeOffset.UtcNow,
                ControlLoginMethods.Device,
                UserCode: null)
        };
        await using var coordinator = new OfficialLoginCoordinator(client, TimeSpan.FromMilliseconds(1));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.StartAsync("ChatGPT", ControlLoginMethods.Device, null));

        Assert.Contains("device code", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("login-1", client.CanceledLoginId);
    }

    [Fact]
    public async Task Wait_returns_completed_account_after_polling_pending_state()
    {
        var client = new FakeOfficialLoginClient
        {
            StartResult = new ControlLoginStart(
                "account-1",
                "login-1",
                "https://auth.openai.com/oauth/authorize?state=state-123",
                DateTimeOffset.UtcNow,
                ControlLoginMethods.Browser)
        };
        client.Statuses.Enqueue(new ControlLoginStatus(
            "login-1", "pending", "account-1", null, null, null, DateTimeOffset.UtcNow));
        client.Statuses.Enqueue(new ControlLoginStatus(
            "login-1", "completed", "account-1", "alice@example.com", "plus", null, DateTimeOffset.UtcNow));
        await using var coordinator = new OfficialLoginCoordinator(client, TimeSpan.FromMilliseconds(1));
        _ = await coordinator.StartAsync("ChatGPT", ControlLoginMethods.Browser, null);

        var result = await coordinator.WaitForCompletionAsync();

        Assert.Equal("completed", result.State);
        Assert.Equal("alice@example.com", result.Email);
        Assert.Equal(2, client.StatusReadCount);
    }

    [Fact]
    public async Task Browser_open_failure_cancels_pending_official_login()
    {
        var client = new FakeOfficialLoginClient
        {
            StartResult = new ControlLoginStart(
                "account-1",
                "login-1",
                "https://auth.openai.com/oauth/authorize?state=state-123",
                DateTimeOffset.UtcNow,
                ControlLoginMethods.Browser),
            OpenFailure = new InvalidOperationException("browser unavailable")
        };
        await using var coordinator = new OfficialLoginCoordinator(client, TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.StartAsync("ChatGPT", ControlLoginMethods.Browser, null));

        Assert.Equal("login-1", client.CanceledLoginId);
    }

    private sealed class FakeOfficialLoginClient : IOfficialLoginClient
    {
        public ControlLoginStart StartResult { get; set; } = null!;
        public Exception? OpenFailure { get; set; }
        public Queue<ControlLoginStatus> Statuses { get; } = new();
        public string? SeenMethod { get; private set; }
        public string? SeenProxyUrl { get; private set; }
        public string? OpenedUrl { get; private set; }
        public string? CanceledLoginId { get; private set; }
        public int StatusReadCount { get; private set; }

        public Task<ControlLoginStart> StartOnboardAsync(
            string alias,
            string loginMethod,
            string? proxyUrl,
            CancellationToken cancellationToken = default)
        {
            SeenMethod = loginMethod;
            SeenProxyUrl = proxyUrl;
            return Task.FromResult(StartResult);
        }

        public Task<ControlLoginStatus> GetLoginStatusAsync(
            string loginId,
            CancellationToken cancellationToken = default)
        {
            StatusReadCount++;
            return Task.FromResult(Statuses.Count > 0
                ? Statuses.Dequeue()
                : new ControlLoginStatus(loginId, "pending", StartResult.AccountId, null, null, null, DateTimeOffset.UtcNow));
        }

        public Task<ControlLoginStatus> CancelLoginAsync(
            string loginId,
            CancellationToken cancellationToken = default)
        {
            CanceledLoginId = loginId;
            return Task.FromResult(new ControlLoginStatus(
                loginId, "failed", StartResult.AccountId, null, null, "canceled", DateTimeOffset.UtcNow));
        }

        public Task<IAsyncDisposable> OpenLoginUrlAsync(
            string url,
            string? proxyUrl,
            CancellationToken cancellationToken = default)
        {
            OpenedUrl = url;
            return OpenFailure is null
                ? Task.FromResult<IAsyncDisposable>(new FakeBrowser())
                : Task.FromException<IAsyncDisposable>(OpenFailure);
        }
    }

    private sealed class FakeBrowser : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
