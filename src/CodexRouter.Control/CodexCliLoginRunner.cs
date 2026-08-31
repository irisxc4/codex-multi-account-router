using System.Diagnostics;
using System.Text;

namespace CodexRouter.Control;

public sealed record CodexCliLoginResult(bool Succeeded, int? ExitCode, string? Error);

public interface ICodexCliLoginRunner
{
    Task<CodexCliLoginResult> RunAsync(
        string codexExecutable,
        string codexHome,
        bool deviceAuth = false,
        string? proxyUrl = null,
        CancellationToken cancellationToken = default);
}

public sealed class CodexCliLoginRunner : ICodexCliLoginRunner
{
    public async Task<CodexCliLoginResult> RunAsync(
        string codexExecutable,
        string codexHome,
        bool deviceAuth = false,
        string? proxyUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(codexExecutable) || !File.Exists(codexExecutable))
        {
            return new CodexCliLoginResult(false, null, "Official Codex CLI executable was not found.");
        }
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            return new CodexCliLoginResult(false, null, "CODEX_HOME is required for account login.");
        }

        Directory.CreateDirectory(codexHome);
        var startInfo = new ProcessStartInfo
        {
            FileName = codexExecutable,
            UseShellExecute = false,
            // Device-code login must remain visible so the official Codex CLI itself
            // displays the one-time code. Router never parses or stores that code.
            CreateNoWindow = !deviceAuth,
            RedirectStandardOutput = !deviceAuth,
            // Keep device-code stdout attached to the official CLI window so Router
            // never sees the one-time code. Stderr is safe-filtered for actionable errors.
            RedirectStandardError = true,
            WorkingDirectory = codexHome
        };
        startInfo.ArgumentList.Add("login");
        if (deviceAuth)
        {
            startInfo.ArgumentList.Add("--device-auth");
        }
        startInfo.Environment["CODEX_HOME"] = codexHome;
        foreach (var name in new[]
        {
            "CODEX_CLI_PATH", "CODEX_ACCESS_TOKEN", "OPENAI_BASE_URL", "OPENAI_API_KEY",
            "OPENAI_ORG_ID", "OPENAI_PROJECT_ID", "ANTHROPIC_BASE_URL", "ANTHROPIC_API_KEY",
            "ANTHROPIC_AUTH_TOKEN", "CODEX_THREAD_ID"
        })
        {
            startInfo.Environment.Remove(name);
        }
        CodexLoginProxy.Apply(startInfo, proxyUrl);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        if (!deviceAuth)
        {
            process.OutputDataReceived += (_, _) => { };
        }
        process.ErrorDataReceived += (_, e) => AppendSafeDiagnostic(stderr, e.Data);

        try
        {
            if (!process.Start())
            {
                return new CodexCliLoginResult(false, null, "Windows could not start the official Codex login process.");
            }
            if (!deviceAuth)
            {
                process.BeginOutputReadLine();
            }
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                return new CodexCliLoginResult(true, 0, null);
            }

            var error = FirstUsefulLine(stderr) ?? (deviceAuth
                ? $"Official Codex device-code login exited with code {process.ExitCode}."
                : $"Official Codex login exited with code {process.ExitCode}.");
            return new CodexCliLoginResult(false, process.ExitCode, error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new CodexCliLoginResult(false, null, ex.Message);
        }
    }

    private static void AppendSafeDiagnostic(StringBuilder builder, string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || builder.Length >= 8192) return;
        var trimmed = line.Trim();
        if (trimmed.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("code_challenge", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("state=", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (builder.Length > 0) builder.AppendLine();
        builder.Append(trimmed);
        if (builder.Length > 8192) builder.Length = 8192;
    }

    private static string? FirstUsefulLine(StringBuilder builder)
    {
        foreach (var line in builder.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return null;
    }
}
