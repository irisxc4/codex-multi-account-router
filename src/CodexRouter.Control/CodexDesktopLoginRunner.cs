using System.Diagnostics;

namespace CodexRouter.Control;

public sealed record CodexDesktopLoginResult(bool Succeeded, string? Error);

public interface ICodexDesktopLoginRunner
{
    Task<CodexDesktopLoginResult> RunAsync(
        string desktopExecutable,
        string codexExecutable,
        string codexHome,
        TimeSpan timeout,
        string? proxyUrl = null,
        CancellationToken cancellationToken = default);
}

public sealed class CodexDesktopLoginRunner : ICodexDesktopLoginRunner
{
    private static readonly string[] ScrubbedEnvironmentVariables =
    {
        "CODEX_CLI_PATH",
        "CODEX_ACCESS_TOKEN",
        "OPENAI_BASE_URL",
        "OPENAI_API_KEY",
        "OPENAI_ORG_ID",
        "OPENAI_PROJECT_ID",
        "ANTHROPIC_BASE_URL",
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "CODEX_THREAD_ID"
    };

    public async Task<CodexDesktopLoginResult> RunAsync(
        string desktopExecutable,
        string codexExecutable,
        string codexHome,
        TimeSpan timeout,
        string? proxyUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new CodexDesktopLoginResult(false, "Official Codex Desktop login is currently supported only on Windows.");
        }
        if (string.IsNullOrWhiteSpace(desktopExecutable) || !File.Exists(desktopExecutable))
        {
            return new CodexDesktopLoginResult(false, "Official Codex Desktop executable was not found.");
        }
        if (string.IsNullOrWhiteSpace(codexExecutable) || !File.Exists(codexExecutable))
        {
            return new CodexDesktopLoginResult(false, "Official Codex CLI executable was not found.");
        }
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            return new CodexDesktopLoginResult(false, "CODEX_HOME is required for Desktop account login.");
        }
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        codexHome = Path.GetFullPath(codexHome);
        Directory.CreateDirectory(codexHome);
        var userDataDirectory = Path.Combine(codexHome, "desktop-user-data");
        Directory.CreateDirectory(userDataDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(desktopExecutable),
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(desktopExecutable))!
        };
        startInfo.ArgumentList.Add($"--user-data-dir={userDataDirectory}");
        var normalizedProxy = CodexLoginProxy.Normalize(proxyUrl);
        if (normalizedProxy is not null)
        {
            startInfo.ArgumentList.Add($"--proxy-server={normalizedProxy}");
        }
        startInfo.Environment["CODEX_HOME"] = codexHome;
        foreach (var name in ScrubbedEnvironmentVariables)
        {
            startInfo.Environment.Remove(name);
        }
        CodexLoginProxy.Apply(startInfo, normalizedProxy);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                return new CodexDesktopLoginResult(false, "Windows could not start the official Codex Desktop login window.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new CodexDesktopLoginResult(false, ex.Message);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                timeoutCts.Token.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    return new CodexDesktopLoginResult(false, "Official Codex Desktop login window closed before ChatGPT sign-in completed.");
                }

                if (await IsChatGptLoggedInAsync(codexExecutable, codexHome, normalizedProxy, timeoutCts.Token).ConfigureAwait(false))
                {
                    return new CodexDesktopLoginResult(true, null);
                }

                await Task.Delay(TimeSpan.FromSeconds(2), timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CodexDesktopLoginResult(false, "Official Codex Desktop login timed out.");
        }
        finally
        {
            TryKill(process);
        }
    }

    internal static async Task<bool> IsChatGptLoggedInAsync(
        string codexExecutable,
        string codexHome,
        string? proxyUrl,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = codexExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = codexHome
        };
        startInfo.ArgumentList.Add("login");
        startInfo.ArgumentList.Add("status");
        startInfo.Environment["CODEX_HOME"] = codexHome;
        foreach (var name in ScrubbedEnvironmentVariables)
        {
            startInfo.Environment.Remove(name);
        }
        CodexLoginProxy.Apply(startInfo, proxyUrl);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) return false;
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
            return process.ExitCode == 0 && stdout.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Best-effort cleanup. The broker owns only the isolated Desktop process it started.
        }
    }
}
