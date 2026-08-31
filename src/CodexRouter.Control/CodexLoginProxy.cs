using System.Diagnostics;

namespace CodexRouter.Control;

internal static class CodexLoginProxy
{
    private static readonly string[] ProxyEnvironmentKeys =
    {
        "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy",
        "ALL_PROXY", "all_proxy", "NO_PROXY", "no_proxy"
    };

    public static string? Normalize(string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl)) return null;
        if (!Uri.TryCreate(proxyUrl.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Login proxy URL is invalid.", nameof(proxyUrl));
        }
        if (uri.Scheme is not ("http" or "https" or "socks4" or "socks5"))
        {
            throw new ArgumentException("Login proxy must use http, https, socks4, or socks5.", nameof(proxyUrl));
        }
        if (string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length > 0 || uri.AbsolutePath is not ("" or "/") || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Login proxy must be a host:port URL without credentials, path, query, or fragment.", nameof(proxyUrl));
        }
        if (uri.Port <= 0)
        {
            throw new ArgumentException("Login proxy port is required.", nameof(proxyUrl));
        }
        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped).TrimEnd('/');
    }

    public static void Apply(ProcessStartInfo startInfo, string? proxyUrl)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var environment = CreateEnvironment(proxyUrl);
        if (environment is null) return;
        foreach (var pair in environment)
        {
            if (pair.Value is null) startInfo.Environment.Remove(pair.Key);
            else startInfo.Environment[pair.Key] = pair.Value;
        }
    }

    public static IReadOnlyDictionary<string, string?> CreateEnvironment(string? proxyUrl)
    {
        var normalized = Normalize(proxyUrl);
        var environment = ProxyEnvironmentKeys.ToDictionary(static key => key, static _ => (string?)null, StringComparer.Ordinal);
        if (normalized is null) return environment;

        var uri = new Uri(normalized);
        if (uri.Scheme is "socks4" or "socks5")
        {
            environment["ALL_PROXY"] = normalized;
            environment["all_proxy"] = normalized;
        }
        else
        {
            environment["HTTP_PROXY"] = normalized;
            environment["HTTPS_PROXY"] = normalized;
            environment["http_proxy"] = normalized;
            environment["https_proxy"] = normalized;
        }

        const string noProxy = "localhost,.localhost,127.0.0.1,127.0.0.0/8,::1,[::1]";
        environment["NO_PROXY"] = noProxy;
        environment["no_proxy"] = noProxy;
        return environment;
    }
}
