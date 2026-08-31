using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CodexRouter.Domain;

namespace CodexRouter.Protocol;

public sealed record CodexBinaryDiscoveryOptions(
    string? ExplicitPath = null,
    TimeSpan? VersionProbeTimeout = null,
    string? LocalAppDataOverride = null,
    string? PathEnvironmentOverride = null,
    bool IgnoreCodexCliPath = false)
{
    public TimeSpan EffectiveVersionProbeTimeout => VersionProbeTimeout ?? TimeSpan.FromSeconds(5);
}

public sealed record BinaryDiscoveryAttempt(string Path, string Source, string? Failure);

public sealed record BinaryDiscoveryResult(
    BinaryIdentity? Binary,
    IReadOnlyList<BinaryDiscoveryAttempt> Attempts,
    string? Error)
{
    public bool Succeeded => Binary is not null;
}

public sealed class CodexBinaryDiscovery
{
    private static readonly Regex VersionPattern = new(
        @"^codex-cli\s+(?<version>\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private readonly IProcessRunner _processRunner;

    public CodexBinaryDiscovery(IProcessRunner? processRunner = null)
    {
        _processRunner = processRunner ?? new SystemProcessRunner();
    }

    public async Task<BinaryDiscoveryResult> DiscoverAsync(
        CodexBinaryDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new CodexBinaryDiscoveryOptions();
        var attempts = new List<BinaryDiscoveryAttempt>();
        var candidates = BuildCandidates(options).ToArray();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var validation = await ValidateCandidateAsync(candidate.Path, options.EffectiveVersionProbeTimeout, cancellationToken)
                .ConfigureAwait(false);

            attempts.Add(new BinaryDiscoveryAttempt(candidate.Path, candidate.Source, validation.Error));
            if (validation.Binary is not null)
            {
                return new BinaryDiscoveryResult(validation.Binary, attempts, null);
            }

            if (candidate.IsAuthoritative)
            {
                return new BinaryDiscoveryResult(null, attempts,
                    $"Configured Codex binary from {candidate.Source} is invalid: {validation.Error}");
            }
        }

        return new BinaryDiscoveryResult(null, attempts,
            candidates.Length == 0
                ? "No Codex binary candidates were found."
                : "No discovered candidate was a valid Codex CLI binary.");
    }

    public async Task<(BinaryIdentity? Binary, string? Error)> ValidateCandidateAsync(
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (null, "Path is empty.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, $"Path is invalid: {ex.Message}");
        }

        if (!File.Exists(fullPath))
        {
            return (null, "File does not exist.");
        }

        var versionResult = await _processRunner.RunAsync(
            new ProcessRequest(fullPath, new[] { "--version" }, timeout),
            cancellationToken).ConfigureAwait(false);

        if (versionResult.StartException is not null)
        {
            return (null, $"Failed to start binary: {versionResult.StartException.Message}");
        }

        if (versionResult.TimedOut)
        {
            return (null, $"Version probe timed out after {timeout.TotalSeconds:0.###} seconds.");
        }

        if (versionResult.ExitCode != 0)
        {
            return (null, $"Version probe exited with code {versionResult.ExitCode}: {TrimDiagnostic(versionResult.StandardError)}");
        }

        var output = string.IsNullOrWhiteSpace(versionResult.StandardOutput)
            ? versionResult.StandardError
            : versionResult.StandardOutput;
        var match = VersionPattern.Match(output ?? string.Empty);
        if (!match.Success)
        {
            return (null, $"Executable did not identify itself as codex-cli. Output: {TrimDiagnostic(output)}");
        }

        var info = new FileInfo(fullPath);
        var sha256 = await ComputeSha256Async(fullPath, cancellationToken).ConfigureAwait(false);
        var identity = new BinaryIdentity(
            fullPath,
            match.Groups["version"].Value,
            sha256,
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));

        return (identity, null);
    }

    private static IEnumerable<BinaryCandidate> BuildCandidates(CodexBinaryDiscoveryOptions options)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(options.ExplicitPath))
        {
            var normalized = NormalizeCandidate(options.ExplicitPath);
            if (seen.Add(normalized))
            {
                yield return new BinaryCandidate(normalized, "explicit override", true);
            }
            yield break;
        }

        if (!options.IgnoreCodexCliPath)
        {
            var configured = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var normalized = NormalizeCandidate(configured);
                // CODEX_CLI_PATH is the Desktop integration point used by Codex Router itself.
                // Never recursively identify the canonical Router shim as the native Codex CLI.
                if (!string.Equals(Path.GetFileName(normalized), "codex-route.exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (seen.Add(normalized))
                    {
                        yield return new BinaryCandidate(normalized, "CODEX_CLI_PATH", true);
                    }
                    yield break;
                }
            }
        }

        var localAppData = options.LocalAppDataOverride
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var binRoot = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
            if (Directory.Exists(binRoot))
            {
                IEnumerable<string> installed = Array.Empty<string>();
                try
                {
                    installed = Directory.EnumerateFiles(binRoot, "codex.exe", SearchOption.AllDirectories)
                        .OrderByDescending(static path => File.GetLastWriteTimeUtc(path));
                }
                catch (UnauthorizedAccessException)
                {
                    // Discovery continues with PATH candidates.
                }
                catch (IOException)
                {
                    // Discovery continues with PATH candidates.
                }

                foreach (var path in installed)
                {
                    var normalized = NormalizeCandidate(path);
                    if (seen.Add(normalized))
                    {
                        yield return new BinaryCandidate(normalized, "Codex Desktop local install", false);
                    }
                }
            }
        }

        var pathEnvironment = options.PathEnvironmentOverride ?? Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnvironment))
        {
            foreach (var directory in pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory.Trim('"'), OperatingSystem.IsWindows() ? "codex.exe" : "codex");
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (!File.Exists(candidate))
                {
                    continue;
                }

                var normalized = NormalizeCandidate(candidate);
                if (seen.Add(normalized))
                {
                    yield return new BinaryCandidate(normalized, "PATH", false);
                }
            }
        }
    }

    private static string NormalizeCandidate(string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string TrimDiagnostic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<empty>";
        }

        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 300 ? normalized : normalized[..300] + "…";
    }

    private sealed record BinaryCandidate(string Path, string Source, bool IsAuthoritative);
}
