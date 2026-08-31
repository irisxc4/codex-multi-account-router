using CodexRouter.Domain;
using CodexRouter.Host;
using CodexRouter.Storage;
using Microsoft.Data.Sqlite;

namespace CodexRouter.Maintenance;

public sealed record RecoveryOptions(
    string? PackageDirectory = null,
    bool ReinstallMissingBinaries = true,
    bool RestoreBrokenIntegration = true,
    bool RecreateCorruptDatabase = false,
    bool ForceIntegrationRestore = false);

public sealed class RecoveryService
{
    private readonly InstallLayout _layout;
    private readonly RouterPaths _paths;
    private readonly IStartupRegistration _startup;
    private readonly CodexDesktopIntegrationManager _integration;
    private readonly Func<CancellationToken, Task<CompatibilityReport>> _compatibilityProbe;

    public RecoveryService(
        string root,
        IStartupRegistration? startup = null,
        CodexDesktopIntegrationManager? integration = null,
        Func<CancellationToken, Task<CompatibilityReport>>? compatibilityProbe = null)
    {
        _layout = new InstallLayout(root);
        _paths = new RouterPaths(root);
        _startup = startup ?? new WindowsStartupRegistration();
        _integration = integration ?? new CodexDesktopIntegrationManager(_paths);
        _compatibilityProbe = compatibilityProbe ?? (ct =>
            new HostCompatibilityProbe(nativeLocator: new NativeCodexLocator(_paths)).ProbeAsync(ct));
    }

    public async Task<RecoveryReport> RepairAsync(
        RecoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new RecoveryOptions();
        Directory.CreateDirectory(_layout.Root);
        var items = new List<RecoveryItem>();

        await RepairBinariesAsync(options, items, cancellationToken).ConfigureAwait(false);
        await RepairIntegrationAsync(options, items, cancellationToken).ConfigureAwait(false);
        await CheckStorageAndProfilesAsync(options, items, cancellationToken).ConfigureAwait(false);
        await CheckCompatibilityAsync(items, cancellationToken).ConfigureAwait(false);
        CheckStartup(items);

        return new RecoveryReport(
            items,
            items.Count(static item => item.Status == "repaired"),
            items.Count(static item => item.Status == "conflict"),
            DateTimeOffset.UtcNow);
    }

    private async Task RepairBinariesAsync(
        RecoveryOptions options,
        ICollection<RecoveryItem> items,
        CancellationToken cancellationToken)
    {
        var routeExists = File.Exists(_layout.RouteExecutable);
        var overlayExists = File.Exists(_layout.OverlayExecutable);
        if (routeExists && overlayExists)
        {
            items.Add(new RecoveryItem("binary", "installed", "ok", "Router shim and overlay executables are present."));
            return;
        }

        if (!options.ReinstallMissingBinaries || string.IsNullOrWhiteSpace(options.PackageDirectory))
        {
            items.Add(new RecoveryItem(
                "binary",
                "installed",
                "conflict",
                $"Missing binaries: {string.Join(", ", MissingBinaryNames(routeExists, overlayExists))}. Supply a verified package to repair them."));
            return;
        }

        var package = Path.GetFullPath(options.PackageDirectory);
        var startupWasEnabled = overlayExists && _startup.IsEnabled(_layout.OverlayExecutable);
        var installer = new InstallationManager(_layout.Root, _startup, _integration);
        var installed = await installer.InstallAsync(package, startupWasEnabled, cancellationToken).ConfigureAwait(false);
        items.Add(new RecoveryItem(
            "binary",
            "installed",
            "repaired",
            $"Reinstalled verified package {installed.Version}."));
    }

    private async Task RepairIntegrationAsync(
        RecoveryOptions options,
        ICollection<RecoveryItem> items,
        CancellationToken cancellationToken)
    {
        var probe = _integration.Probe(_layout.RouteExecutable);
        if (probe.Status == DesktopIntegrationStatus.Active && probe.ShimExists)
        {
            items.Add(new RecoveryItem("integration", "CODEX_CLI_PATH", "ok", "Codex Desktop redirect is active and the shim exists."));
            return;
        }

        if (probe.Status == DesktopIntegrationStatus.ShimMissing && options.RestoreBrokenIntegration)
        {
            var restored = await _integration.DisableAsync(options.ForceIntegrationRestore, cancellationToken).ConfigureAwait(false);
            var status = restored.Status == DesktopIntegrationStatus.Conflict ? "conflict" : "repaired";
            items.Add(new RecoveryItem("integration", "CODEX_CLI_PATH", status, restored.Message));
            return;
        }

        if (probe.Status == DesktopIntegrationStatus.Conflict)
        {
            items.Add(new RecoveryItem(
                "integration",
                "CODEX_CLI_PATH",
                "conflict",
                probe.Message ?? "Integration state conflicts with the current user environment value."));
            return;
        }

        if (probe.Status == DesktopIntegrationStatus.StateMissing && probe.StateExists)
        {
            if (options.RestoreBrokenIntegration && options.ForceIntegrationRestore)
            {
                var restored = await _integration.DisableAsync(force: true, cancellationToken).ConfigureAwait(false);
                items.Add(new RecoveryItem("integration", "state", "repaired", restored.Message));
            }
            else
            {
                items.Add(new RecoveryItem(
                    "integration",
                    "state",
                    "conflict",
                    "A stale integration-state.json exists but the current environment no longer points at the Router. Force restore is required before changing external state."));
            }
            return;
        }

        items.Add(new RecoveryItem(
            "integration",
            "CODEX_CLI_PATH",
            probe.Status is DesktopIntegrationStatus.NotConfigured or DesktopIntegrationStatus.RedirectedElsewhere ? "ok" : "warning",
            probe.Message ?? probe.Status.ToString()));
    }

    private async Task CheckStorageAndProfilesAsync(
        RecoveryOptions options,
        ICollection<RecoveryItem> items,
        CancellationToken cancellationToken)
    {
        StorageDatabase? database = null;
        try
        {
            database = new StorageDatabase(new StorageOptions(_paths.DatabasePath));
            await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await VerifyIntegrityAsync(_paths.DatabasePath, cancellationToken).ConfigureAwait(false);
            items.Add(new RecoveryItem("storage", "router.db", "ok", "Router database opened and passed SQLite integrity_check."));
        }
        catch (Exception ex) when (ex is StorageCorruptionException or SqliteException or IOException or InvalidDataException)
        {
            if (!options.RecreateCorruptDatabase)
            {
                items.Add(new RecoveryItem("storage", "router.db", "conflict", $"Database recovery requires explicit opt-in: {ex.Message}"));
                return;
            }

            SqliteConnection.ClearAllPools();
            var backup = QuarantineDatabase(cancellationToken);
            database = new StorageDatabase(new StorageOptions(_paths.DatabasePath));
            await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            items.Add(new RecoveryItem("storage", "router.db", "repaired", $"Corrupt database was quarantined to '{backup}' and a new database was initialized."));
        }

        if (database is null) return;
        var accounts = await new RouterRepository(database).ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var account in accounts)
        {
            if (Directory.Exists(account.Profile.CodexHome))
            {
                items.Add(new RecoveryItem("profile", account.Profile.Id.Value, "ok", $"Profile '{account.Profile.Alias}' CODEX_HOME exists."));
            }
            else
            {
                items.Add(new RecoveryItem(
                    "profile",
                    account.Profile.Id.Value,
                    "conflict",
                    $"Profile '{account.Profile.Alias}' CODEX_HOME is missing. Authentication state cannot be reconstructed automatically; re-login or restore the profile directory."));
            }
        }
    }

    private async Task CheckCompatibilityAsync(ICollection<RecoveryItem> items, CancellationToken cancellationToken)
    {
        try
        {
            var compatibility = await _compatibilityProbe(cancellationToken).ConfigureAwait(false);
            var status = compatibility.State switch
            {
                CompatibilityState.Compatible => "ok",
                CompatibilityState.Degraded => "warning",
                _ => "conflict"
            };
            items.Add(new RecoveryItem(
                "compatibility",
                compatibility.Binary?.Version ?? "unknown",
                status,
                compatibility.State is CompatibilityState.Compatible or CompatibilityState.Degraded
                    ? $"Codex AppServer compatibility is {compatibility.State}."
                    : $"Codex AppServer compatibility is {compatibility.State}; Router app-server will fall back to native Codex."));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            items.Add(new RecoveryItem("compatibility", "probe", "conflict", ex.Message));
        }
    }

    private void CheckStartup(ICollection<RecoveryItem> items)
    {
        if (!File.Exists(_layout.OverlayExecutable))
        {
            items.Add(new RecoveryItem("startup", "overlay", "warning", "Overlay executable is missing; startup registration is not enabled."));
            return;
        }

        items.Add(new RecoveryItem(
            "startup",
            "overlay",
            _startup.IsEnabled(_layout.OverlayExecutable) ? "ok" : "warning",
            _startup.IsEnabled(_layout.OverlayExecutable)
                ? "Overlay startup registration is active."
                : "Overlay startup registration is disabled by user choice."));
    }

    private string QuarantineDatabase(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var quarantine = _paths.DatabasePath + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        var movedAny = false;
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var source = _paths.DatabasePath + suffix;
            if (!File.Exists(source)) continue;
            var destination = quarantine + suffix;
            File.Move(source, destination, overwrite: false);
            movedAny = true;
        }
        if (!movedAny) throw new FileNotFoundException("Router database did not exist when quarantine was requested.", _paths.DatabasePath);
        return quarantine;
    }

    private static async Task VerifyIntegrityAsync(string databasePath, CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private
        };
        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SQLite integrity_check returned '{result ?? "<null>"}'.");
    }

    private static IEnumerable<string> MissingBinaryNames(bool routeExists, bool overlayExists)
    {
        if (!routeExists) yield return "codex-route.exe";
        if (!overlayExists) yield return "CodexRouterOverlay.exe";
    }
}
