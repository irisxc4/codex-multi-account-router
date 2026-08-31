using CodexRouter.Workers;
using Xunit;

namespace CodexRouter.Workers.Tests;

public sealed class ProfileWorkerNetworkRouteTests
{
    [Fact]
    public async Task Profile_proxy_route_round_trips_to_worker_environment_and_can_be_removed()
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-router-route-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        try
        {
            const string proxy = "http://127.0.0.1:7897";
            await ProfileWorkerNetworkRoute.SaveProxyAsync(home, proxy);

            var environment = ProfileWorkerNetworkRoute.LoadEnvironment(home);

            Assert.NotNull(environment);
            Assert.Equal(proxy, environment!["HTTP_PROXY"]);
            Assert.Equal(proxy, environment["HTTPS_PROXY"]);
            Assert.Null(environment["ALL_PROXY"]);
            Assert.Contains("127.0.0.1", environment["NO_PROXY"], StringComparison.Ordinal);

            await ProfileWorkerNetworkRoute.DeleteAsync(home);
            Assert.Null(ProfileWorkerNetworkRoute.LoadEnvironment(home));
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Explicit_direct_route_clears_inherited_proxy_environment()
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-router-route-direct-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        try
        {
            await ProfileWorkerNetworkRoute.SaveProxyAsync(home, proxyUrl: null);

            var environment = ProfileWorkerNetworkRoute.LoadEnvironment(home);

            Assert.NotNull(environment);
            Assert.Null(environment!["HTTP_PROXY"]);
            Assert.Null(environment["HTTPS_PROXY"]);
            Assert.Null(environment["ALL_PROXY"]);
            Assert.Null(environment["NO_PROXY"]);
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Profile_proxy_route_rejects_credentials_and_non_http_schemes()
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-router-route-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                ProfileWorkerNetworkRoute.SaveProxyAsync(home, "http://user:password@127.0.0.1:7897"));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                ProfileWorkerNetworkRoute.SaveProxyAsync(home, "socks5://127.0.0.1:7897"));
            Assert.Null(ProfileWorkerNetworkRoute.LoadEnvironment(home));
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }
}
