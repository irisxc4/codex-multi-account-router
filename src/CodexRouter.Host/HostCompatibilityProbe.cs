using System.Diagnostics;
using CodexRouter.Domain;
using CodexRouter.Protocol;

namespace CodexRouter.Host;

public sealed class HostCompatibilityProbe
{
    private static readonly string[] RequiredMethods =
    {
        "initialize",
        "thread/start",
        "thread/resume",
        "thread/fork",
        "thread/list",
        "thread/read",
        "turn/start",
        "turn/interrupt",
        "account/read",
        "account/login/start",
        "account/rateLimits/read"
    };

    private readonly NativeCodexLocator _nativeLocator;

    public HostCompatibilityProbe(CodexBinaryDiscovery? binaryDiscovery = null, NativeCodexLocator? nativeLocator = null)
    {
        _nativeLocator = nativeLocator ?? new NativeCodexLocator(discovery: binaryDiscovery);
    }

    public async Task<CompatibilityReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        var discovery = await _nativeLocator.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (!discovery.Succeeded || discovery.Binary is null)
        {
            return new CompatibilityReport(
                CompatibilityState.Incompatible,
                null,
                null,
                checkedAt,
                Array.Empty<CompatibilityIssue>(),
                RequiredMethods,
                Array.Empty<string>());
        }

        var temp = Path.Combine(Path.GetTempPath(), $"codex-router-host-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = discovery.Binary.Path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("generate-json-schema");
            startInfo.ArgumentList.Add("--out");
            startInfo.ArgumentList.Add(temp);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start Codex schema generator.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            _ = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return new CompatibilityReport(
                    CompatibilityState.Incompatible,
                    discovery.Binary,
                    null,
                    checkedAt,
                    Array.Empty<CompatibilityIssue>(),
                    RequiredMethods,
                    new[] { $"schema generation failed with exit {process.ExitCode}: {Trim(stderr, 500)}" });
            }

            var schemaFiles = Directory.EnumerateFiles(temp, "*.json", SearchOption.AllDirectories).ToArray();
            var corpus = new System.Text.StringBuilder();
            foreach (var file in schemaFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                corpus.Append(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false));
                corpus.Append('\n');
            }

            var text = corpus.ToString();
            var missing = RequiredMethods
                .Where(method => !text.Contains($"\"{method}\"", StringComparison.Ordinal))
                .ToArray();
            return new CompatibilityReport(
                missing.Length == 0 ? CompatibilityState.Compatible : CompatibilityState.Incompatible,
                discovery.Binary,
                null,
                checkedAt,
                Array.Empty<CompatibilityIssue>(),
                missing,
                Array.Empty<string>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new CompatibilityReport(
                CompatibilityState.Incompatible,
                discovery.Binary,
                null,
                checkedAt,
                Array.Empty<CompatibilityIssue>(),
                RequiredMethods,
                new[] { Trim(ex.Message, 500) });
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];
}
