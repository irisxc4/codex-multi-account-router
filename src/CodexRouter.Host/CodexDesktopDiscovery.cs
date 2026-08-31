using System.Diagnostics;

namespace CodexRouter.Host;

public sealed record CodexDesktopProcessInfo(
    int ProcessId,
    string? ExecutablePath,
    string? MainWindowTitle,
    bool HasMainWindow);

public sealed record CodexDesktopDiscoveryResult(
    IReadOnlyList<CodexDesktopProcessInfo> RunningProcesses,
    IReadOnlyList<string> CandidateInstallRoots,
    bool IsRunning,
    DateTimeOffset ObservedAt);

public sealed class CodexDesktopDiscovery
{
    public Task<CodexDesktopDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var processes = new List<CodexDesktopProcessInfo>();
        if (OperatingSystem.IsWindows())
        {
            var candidates = Process.GetProcessesByName("Codex")
                .Concat(Process.GetProcessesByName("ChatGPT"))
                .GroupBy(static process => process.Id)
                .Select(static group => group.First())
                .ToArray();
            try
            {
                foreach (var process in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var processName = process.ProcessName;
                    var packageFamily = CodexDesktopProcessIdentity.TryGetPackageFamilyName(process.Id);
                    var path = CodexDesktopProcessIdentity.TryGetProcessImagePath(process.Id);
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        try { path = process.MainModule?.FileName; }
                        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                    }
                    if (!CodexDesktopProcessIdentity.Matches(processName, packageFamily, path))
                    {
                        continue;
                    }

                    string? title = null;
                    try { title = process.MainWindowTitle; } catch (InvalidOperationException) { }
                    IntPtr handle;
                    try { handle = process.MainWindowHandle; } catch (InvalidOperationException) { handle = IntPtr.Zero; }

                    // Chromium child processes use the same packaged executable. Only
                    // the ChatGPT.exe process owning a top-level window is the Codex UI.
                    if (string.Equals(processName, "ChatGPT", StringComparison.OrdinalIgnoreCase) && handle == IntPtr.Zero)
                    {
                        continue;
                    }
                    processes.Add(new CodexDesktopProcessInfo(process.Id, path, title, handle != IntPtr.Zero));
                }
            }
            finally
            {
                foreach (var process in candidates) process.Dispose();
            }
        }

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in processes)
        {
            if (!string.IsNullOrWhiteSpace(process.ExecutablePath))
            {
                var directory = Path.GetDirectoryName(process.ExecutablePath);
                if (!string.IsNullOrWhiteSpace(directory)) roots.Add(directory);
            }
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            AddIfExists(roots, Path.Combine(local, "OpenAI", "Codex"));
            AddIfExists(roots, Path.Combine(local, "Programs", "Codex"));
        }

        return Task.FromResult(new CodexDesktopDiscoveryResult(
            processes.OrderBy(static process => process.ProcessId).ToArray(),
            roots.OrderBy(static root => root, StringComparer.OrdinalIgnoreCase).ToArray(),
            processes.Count > 0,
            DateTimeOffset.UtcNow));
    }

    private static void AddIfExists(ISet<string> roots, string path)
    {
        if (Directory.Exists(path)) roots.Add(Path.GetFullPath(path));
    }
}
