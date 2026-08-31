using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodexRouter.Host;
using CodexRouter.Protocol;
using CodexRouter.Storage;

namespace CodexRouter.Maintenance;

public sealed class DiagnosticsService
{
    private readonly InstallLayout _layout;
    private readonly RouterPaths _paths;
    private readonly DiagnosticRedactor _redactor;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public DiagnosticsService(string root, DiagnosticRedactor? redactor = null)
    {
        _layout = new InstallLayout(root);
        _paths = new RouterPaths(root);
        _redactor = redactor ?? new DiagnosticRedactor();
    }

    public async Task<DiagnosticsBundleResult> CreateBundleAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_layout.DiagnosticsDirectory);
        var staging = Path.Combine(_layout.DiagnosticsDirectory, $".diag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var database = new StorageDatabase(new StorageOptions(_paths.DatabasePath));
            string? storageError = null;
            RouterRepository? repository = null;
            try
            {
                await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
                repository = new RouterRepository(database);
            }
            catch (Exception ex)
            {
                storageError = ex.Message;
            }

            var integration = new CodexDesktopIntegrationManager(_paths).Probe(_layout.RouteExecutable);
            var desktop = await new CodexDesktopDiscovery().DiscoverAsync(cancellationToken).ConfigureAwait(false);
            var binary = await new NativeCodexLocator(_paths).DiscoverAsync(cancellationToken).ConfigureAwait(false);
            var installed = await ReadInstalledManifestAsync(cancellationToken).ConfigureAwait(false);

            object? storageSummary = null;
            if (repository is not null)
            {
                var accounts = await repository.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
                var orphans = await repository.ListUnresolvedOrphanThreadsAsync(5000, cancellationToken).ConfigureAwait(false);
                var migrations = await repository.ListThreadMigrationJobsAsync(limit: 5000, cancellationToken: cancellationToken).ConfigureAwait(false);
                storageSummary = new
                {
                    accountCount = accounts.Count,
                    accounts = accounts.Select(account => new
                    {
                        id = account.Profile.Id.Value,
                        alias = account.Profile.Alias,
                        plan = account.Profile.PlanType,
                        enabled = account.Profile.Enabled,
                        priority = account.Profile.Priority
                    }).ToArray(),
                    unresolvedOrphanCount = orphans.Count,
                    migrationCounts = migrations.GroupBy(static job => job.State)
                        .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal)
                };
            }

            var summary = new
            {
                generatedAt = DateTimeOffset.UtcNow,
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.OSArchitecture.ToString(),
                framework = RuntimeInformation.FrameworkDescription,
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                install = new
                {
                    root = _layout.Root,
                    routeExists = File.Exists(_layout.RouteExecutable),
                    overlayExists = File.Exists(_layout.OverlayExecutable),
                    installedVersion = installed?.Version
                },
                integration,
                desktop,
                codexBinary = binary.Binary is null ? null : new
                {
                    binary.Binary.Path,
                    binary.Binary.Version,
                    binary.Binary.Sha256,
                    binary.Binary.SizeBytes,
                    binary.Binary.LastWriteTimeUtc
                },
                storageError,
                storage = storageSummary,
                exclusions = new[]
                {
                    "router.db is intentionally excluded",
                    "control.token is intentionally excluded",
                    "profile directories/auth.json/keyring material are intentionally excluded",
                    "migration snapshot/handoff content is intentionally excluded"
                }
            };
            await WriteRedactedJsonAsync(Path.Combine(staging, "summary.json"), summary, cancellationToken).ConfigureAwait(false);

            var logCount = await CopyRedactedLogsAsync(staging, cancellationToken).ConfigureAwait(false);
            var readme = """
                Codex Router diagnostics bundle
                ===============================
                This bundle intentionally excludes router.db, control.token, CODEX_HOME profile contents,
                authentication files, cookies/tokens, and migration snapshot/handoff payloads.
                Email addresses and token-like strings in copied text logs are redacted.
                """;
            await File.WriteAllTextAsync(Path.Combine(staging, "README.txt"), readme, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            var zipPath = Path.Combine(_layout.DiagnosticsDirectory, $"codex-router-diagnostics-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(staging, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            var info = new FileInfo(zipPath);
            return new DiagnosticsBundleResult(zipPath, 2 + logCount, info.Length, DateTimeOffset.UtcNow);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }
    }

    private async Task<int> CopyRedactedLogsAsync(string staging, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.LogsRoot)) return 0;
        var output = Path.Combine(staging, "logs");
        Directory.CreateDirectory(output);
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(_paths.LogsRoot, "*", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Take(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.Length > 5 * 1024 * 1024) continue;
            string text;
            try { text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            var redacted = _redactor.Redact(text);
            await File.WriteAllTextAsync(Path.Combine(output, Path.GetFileName(path) + ".redacted.txt"), redacted, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            count++;
        }
        return count;
    }

    private async Task WriteRedactedJsonAsync(string path, object value, CancellationToken cancellationToken)
    {
        var raw = JsonSerializer.Serialize(value, _json);
        await File.WriteAllTextAsync(path, _redactor.Redact(raw), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private async Task<CodexRouterPackageManifest?> ReadInstalledManifestAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_layout.InstalledManifest)) return null;
        try
        {
            await using var stream = new FileStream(_layout.InstalledManifest, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, true);
            return await JsonSerializer.DeserializeAsync<CodexRouterPackageManifest>(stream, _json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }
}
