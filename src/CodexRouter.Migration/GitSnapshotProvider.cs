using System.Diagnostics;
using System.Text;

namespace CodexRouter.Migration;

public interface IGitSnapshotProvider
{
    Task<GitWorkspaceSnapshot> CaptureAsync(string? cwd, CancellationToken cancellationToken = default);
}

public sealed class GitSnapshotProvider : IGitSnapshotProvider
{
    private readonly TimeSpan _commandTimeout;
    private readonly int _maxDiffChars;

    public GitSnapshotProvider(TimeSpan? commandTimeout = null, int maxDiffChars = 80_000)
    {
        _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(8);
        _maxDiffChars = Math.Max(8_000, maxDiffChars);
    }

    public async Task<GitWorkspaceSnapshot> CaptureAsync(string? cwd, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
        {
            return new GitWorkspaceSnapshot(null, null, null, null, Array.Empty<string>());
        }

        var inside = await RunGitAsync(cwd, new[] { "rev-parse", "--is-inside-work-tree" }, cancellationToken).ConfigureAwait(false);
        if (inside.ExitCode != 0 || !string.Equals(inside.Stdout.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return new GitWorkspaceSnapshot(null, null, null, null, Array.Empty<string>());
        }

        var branchTask = RunGitAsync(cwd, new[] { "branch", "--show-current" }, cancellationToken);
        var commitTask = RunGitAsync(cwd, new[] { "rev-parse", "HEAD" }, cancellationToken);
        var statusTask = RunGitAsync(cwd, new[] { "status", "--short", "--untracked-files=all" }, cancellationToken);
        var diffTask = RunGitAsync(cwd, new[] { "diff", "--no-ext-diff", "--binary", "--" }, cancellationToken);
        await Task.WhenAll(branchTask, commitTask, statusTask, diffTask).ConfigureAwait(false);

        var branch = await branchTask.ConfigureAwait(false);
        var commit = await commitTask.ConfigureAwait(false);
        var status = await statusTask.ConfigureAwait(false);
        var diff = await diffTask.ConfigureAwait(false);
        var statusText = status.ExitCode == 0 ? Trim(status.Stdout, 40_000) : null;
        return new GitWorkspaceSnapshot(
            branch.ExitCode == 0 ? EmptyToNull(branch.Stdout.Trim()) : null,
            commit.ExitCode == 0 ? EmptyToNull(commit.Stdout.Trim()) : null,
            statusText,
            diff.ExitCode == 0 ? Trim(diff.Stdout, _maxDiffChars) : null,
            ExtractFiles(statusText));
    }

    private async Task<CommandResult> RunGitAsync(
        string cwd,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return new CommandResult(-1, string.Empty, "git failed to start");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_commandTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new CommandResult(-1, await stdoutTask.ConfigureAwait(false), "git command timed out");
            }
            return new CommandResult(process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new CommandResult(-1, string.Empty, ex.Message);
        }
    }

    private static IReadOnlyList<string> ExtractFiles(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return Array.Empty<string>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in status.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length <= 3) continue;
            var path = line[3..].Trim();
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) path = path[(arrow + 4)..].Trim();
            if (!string.IsNullOrWhiteSpace(path)) files.Add(path.Trim('"'));
        }
        return files.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).Take(200).ToArray();
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string Trim(string value, int maxChars) => value.Length <= maxChars ? value : value[..maxChars] + "\n...[truncated]";
    private sealed record CommandResult(int ExitCode, string Stdout, string Stderr);
}
