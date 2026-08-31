using System.Diagnostics;
using System.IO;

namespace CodexRouter.Overlay;

public sealed class OfficialLoginBrowser : IAsyncDisposable
{
    private readonly Process? _process;
    private readonly string? _userDataDirectory;
    private int _disposed;

    private OfficialLoginBrowser(Process? process, string? userDataDirectory)
    {
        _process = process;
        _userDataDirectory = userDataDirectory;
    }

    public static Task<OfficialLoginBrowser> OpenAsync(
        Uri authUrl,
        string? proxyUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authUrl);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAllowedOfficialLoginUrl(authUrl))
        {
            throw new ArgumentException("Official Codex login URL must use an approved OpenAI/ChatGPT HTTPS host.", nameof(authUrl));
        }

        var browserExecutable = FindChromiumBrowser();
        if (browserExecutable is null)
        {
            if (!string.IsNullOrWhiteSpace(proxyUrl))
            {
                throw new FileNotFoundException("A Chromium browser is required for isolated proxied login. Install or enable Microsoft Edge or Google Chrome, or use login without the per-login proxy.");
            }

            // Last-resort fallback for systems where Edge/Chrome was removed. The official
            // URL is still preserved, but Windows owns the browser network route.
            var opened = Process.Start(new ProcessStartInfo
            {
                FileName = authUrl.AbsoluteUri,
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("Windows did not open the official Codex login URL.");
            opened.Dispose();
            return Task.FromResult(new OfficialLoginBrowser(null, null));
        }

        var normalizedProxy = string.IsNullOrWhiteSpace(proxyUrl) ? null : NormalizeProxy(proxyUrl);
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexRouter",
            "login-browser",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var startInfo = new ProcessStartInfo
        {
            FileName = browserExecutable,
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(browserExecutable)!
        };
        startInfo.ArgumentList.Add($"--user-data-dir={root}");
        if (normalizedProxy is null)
        {
            // Match the explicit direct route used by the isolated Codex app-server.
            startInfo.ArgumentList.Add("--no-proxy-server");
        }
        else
        {
            startInfo.ArgumentList.Add($"--proxy-server={normalizedProxy}");
            startInfo.ArgumentList.Add("--proxy-bypass-list=localhost;127.0.0.1;[::1]");
            // Keep the proxied login on TCP/TLS rather than allowing HTTP/3/QUIC to
            // bypass the explicit per-login HTTP proxy route.
            startInfo.ArgumentList.Add("--disable-quic");
        }
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--new-window");
        // The URL is emitted verbatim by the official Codex app-server. Do not add or rewrite OAuth parameters.
        startInfo.ArgumentList.Add(authUrl.AbsoluteUri);

        try
        {
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not start the isolated login browser.");
            return Task.FromResult(new OfficialLoginBrowser(process, root));
        }
        catch
        {
            TryDelete(root);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
            _process.Dispose();
        }
        if (_userDataDirectory is not null) TryDelete(_userDataDirectory);
        return ValueTask.CompletedTask;
    }

    public static string? FindChromiumBrowser()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool IsAllowedOfficialLoginUrl(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(uri.Host, "auth.openai.com", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeProxy(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "socks4" or "socks5") ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0 || uri.UserInfo.Length > 0)
        {
            throw new ArgumentException("Login proxy URL is invalid.", nameof(value));
        }
        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped).TrimEnd('/');
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
