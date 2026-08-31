using System.Text.Json;

namespace CodexRouter.Workers;

public static class ProfileWorkerNetworkRoute
{
    private const string FileName = ".codex-router-network.json";
    private const string NoProxy = "localhost,.localhost,127.0.0.1,127.0.0.0/8,::1,[::1]";
    private static readonly string[] ProxyKeys =
    {
        "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy", "ALL_PROXY", "all_proxy"
    };

    /// <summary>
    /// Persists an explicit per-profile route. A null proxy means explicit direct mode,
    /// not "inherit the Router process environment".
    /// </summary>
    public static async Task SaveProxyAsync(
        string codexHome,
        string? proxyUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(codexHome)) throw new ArgumentException("CODEX_HOME is required.", nameof(codexHome));
        codexHome = Path.GetFullPath(codexHome);
        Directory.CreateDirectory(codexHome);
        var path = Path.Combine(codexHome, FileName);

        string? normalized = null;
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            if (!Uri.TryCreate(proxyUrl.Trim(), UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") ||
                string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0 || uri.UserInfo.Length > 0 ||
                uri.AbsolutePath is not ("" or "/") || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new ArgumentException("Worker proxy URL must be an HTTP/HTTPS host:port URL without credentials, path, query, or fragment.", nameof(proxyUrl));
            }
            normalized = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped).TrimEnd('/');
        }

        var json = JsonSerializer.Serialize(new RouteFile(normalized is null ? "direct" : "proxy", normalized));
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public static IReadOnlyDictionary<string, string?>? LoadEnvironment(string codexHome)
    {
        if (string.IsNullOrWhiteSpace(codexHome)) return null;
        var path = Path.Combine(Path.GetFullPath(codexHome), FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var route = JsonSerializer.Deserialize<RouteFile>(File.ReadAllText(path));
            if (route is null) return null;

            var environment = ProxyKeys.ToDictionary(static key => key, static _ => (string?)null, StringComparer.Ordinal);
            if (string.Equals(route.Mode, "direct", StringComparison.OrdinalIgnoreCase))
            {
                environment["NO_PROXY"] = null;
                environment["no_proxy"] = null;
                return environment;
            }

            if (!string.Equals(route.Mode, "proxy", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(route.ProxyUrl) ||
                !Uri.TryCreate(route.ProxyUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0)
            {
                return null;
            }

            var proxy = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped).TrimEnd('/');
            environment["HTTP_PROXY"] = proxy;
            environment["HTTPS_PROXY"] = proxy;
            environment["http_proxy"] = proxy;
            environment["https_proxy"] = proxy;
            environment["NO_PROXY"] = NoProxy;
            environment["no_proxy"] = NoProxy;
            return environment;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Task DeleteAsync(string codexHome, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(codexHome)) return Task.CompletedTask;
        var path = Path.Combine(Path.GetFullPath(codexHome), FileName);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private sealed record RouteFile(string Mode, string? ProxyUrl);
}
