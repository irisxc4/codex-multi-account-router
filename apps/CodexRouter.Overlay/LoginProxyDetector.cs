using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace CodexRouter.Overlay;

public static class LoginProxyDetector
{
    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public static string? TryDetectLocalProxyUrl()
    {
        var fromEnvironment = FirstUsable(
            Environment.GetEnvironmentVariable("HTTPS_PROXY"),
            Environment.GetEnvironmentVariable("https_proxy"),
            Environment.GetEnvironmentVariable("HTTP_PROXY"),
            Environment.GetEnvironmentVariable("http_proxy"));
        if (fromEnvironment is not null && IsListening(new Uri(fromEnvironment).Port)) return fromEnvironment;

        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false);
            var proxyServer = key?.GetValue("ProxyServer") as string;
            var candidate = ParseWindowsProxyServer(proxyServer);
            return candidate is not null && IsListening(new Uri(candidate).Port) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    public static string? ParseWindowsProxyServer(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Contains('='))
        {
            foreach (var wanted in new[] { "https", "http" })
            {
                foreach (var entry in trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length == 2 && parts[0].Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        var parsed = NormalizeLoopbackProxy(parts[1]);
                        if (parsed is not null) return parsed;
                    }
                }
            }
            return null;
        }
        return NormalizeLoopbackProxy(trimmed);
    }

    private static string? FirstUsable(params string?[] values)
    {
        foreach (var value in values)
        {
            var parsed = NormalizeLoopbackProxy(value);
            if (parsed is not null) return parsed;
        }
        return null;
    }

    private static string? NormalizeLoopbackProxy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal)) candidate = $"http://{candidate}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            uri.Port <= 0 ||
            !IsLoopbackHost(uri.Host))
        {
            return null;
        }

        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped).TrimEnd('/');
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    private static bool IsListening(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port && IPAddress.IsLoopback(endpoint.Address));
        }
        catch
        {
            return false;
        }
    }
}
