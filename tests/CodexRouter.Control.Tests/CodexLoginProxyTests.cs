using Xunit;

namespace CodexRouter.Control.Tests;

public sealed class CodexLoginProxyTests
{
    [Fact]
    public void Explicit_direct_login_clears_inherited_proxy_environment()
    {
        var environment = CodexLoginProxy.CreateEnvironment(proxyUrl: null);

        Assert.NotNull(environment);
        Assert.All(new[]
        {
            "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy",
            "ALL_PROXY", "all_proxy", "NO_PROXY", "no_proxy"
        }, key => Assert.True(environment!.ContainsKey(key) && environment[key] is null));
    }
}
