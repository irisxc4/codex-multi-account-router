using System.Diagnostics;
using System.Text.Json;
using CodexRouter.Host;

namespace CodexRouter.Maintenance;

public sealed class InstallationManager
{
    private readonly InstallLayout _layout;
    private readonly RouterPaths _routerPaths;
    private readonly PackageVerifier _verifier;
    private readonly IStartupRegistration _startup;
    private readonly CodexDesktopIntegrationManager _integration;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public InstallationManager(
        string root,
        IStartupRegistration? startup = null,
        CodexDesktopIntegrationManager? integration = null,
        PackageVerifier? verifier = null)
    {
        _layout = new InstallLayout(root);
        _routerPaths = new RouterPaths(root);
        _startup = startup ?? new WindowsStartupRegistration();
        _integration = integration ?? new CodexDesktopIntegrationManager(_routerPaths);
        _verifier = verifier ?? new PackageVerifier();
    }

    public InstallLayout Layout => _layout;

    public async Task<InstallResult> InstallAsync(
        string packageDirectory,
        bool enableOverlayStartup = true,
        CancellationToken cancellationToken = default)
    {
        packageDirectory = Path.GetFullPath(packageDirectory);
        var manifest = await _verifier.VerifyAsync(packageDirectory, cancellationToken).ConfigureAwait(false);
        EnsureNotRunningFromInstalledBin();
        EnsureInstalledProcessesStopped();
        Directory.CreateDirectory(_layout.Root);

        var stage = Path.Combine(_layout.Root, $".install-{Guid.NewGuid():N}");
        var backup = Path.Combine(_layout.Root, $".bin-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        var swapped = false;
        try
        {
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.Combine(packageDirectory, file.Name);
                var destination = Path.Combine(stage, file.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: false);
            }

            if (Directory.Exists(_layout.BinDirectory))
            {
                Directory.Move(_layout.BinDirectory, backup);
            }
            Directory.Move(stage, _layout.BinDirectory);
            swapped = true;

            await WriteInstalledManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
            if (enableOverlayStartup)
            {
                _startup.Enable(_layout.OverlayExecutable);
            }
            else
            {
                _startup.Disable();
            }

            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            return new InstallResult(
                true,
                manifest.Version,
                _layout.BinDirectory,
                enableOverlayStartup,
                "Codex Router installed. Desktop redirect remains unchanged until explicitly enabled after an account is added.");
        }
        catch
        {
            if (swapped)
            {
                try
                {
                    if (Directory.Exists(_layout.BinDirectory)) Directory.Delete(_layout.BinDirectory, recursive: true);
                    if (Directory.Exists(backup)) Directory.Move(backup, _layout.BinDirectory);
                }
                catch { }
            }
            throw;
        }
        finally
        {
            TryDeleteDirectory(stage);
            if (!swapped) TryDeleteDirectory(backup);
        }
    }

    public async Task<UninstallResult> UninstallAsync(
        bool removeData = false,
        bool forceIntegrationRestore = false,
        CancellationToken cancellationToken = default)
    {
        EnsureNotRunningFromInstalledBin();
        EnsureInstalledProcessesStopped();
        var probe = _integration.Probe(_layout.RouteExecutable);
        if (probe.StateExists || probe.Status == DesktopIntegrationStatus.Active)
        {
            var disabled = await _integration.DisableAsync(forceIntegrationRestore, cancellationToken).ConfigureAwait(false);
            if (disabled.Status == DesktopIntegrationStatus.Conflict && !forceIntegrationRestore)
            {
                return new UninstallResult(false, true,
                    "Uninstall refused because CODEX_CLI_PATH changed externally. Resolve the integration conflict or retry with force restore.");
            }
        }

        _startup.Disable();
        var changed = false;
        if (Directory.Exists(_layout.BinDirectory))
        {
            var tombstone = Path.Combine(_layout.Root, $".uninstall-{Guid.NewGuid():N}");
            Directory.Move(_layout.BinDirectory, tombstone);
            Directory.Delete(tombstone, recursive: true);
            changed = true;
        }
        if (File.Exists(_layout.InstalledManifest))
        {
            File.Delete(_layout.InstalledManifest);
            changed = true;
        }

        if (removeData)
        {
            TryDeleteDirectory(_layout.Root);
            return new UninstallResult(changed, false, "Codex Router binaries and data were removed; Codex Desktop integration was restored first.");
        }
        return new UninstallResult(changed, true, "Codex Router binaries were removed. Account profiles and Router data were retained.");
    }

    public async Task<CodexRouterPackageManifest?> ReadInstalledManifestAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_layout.InstalledManifest)) return null;
        await using var stream = new FileStream(_layout.InstalledManifest, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, true);
        return await JsonSerializer.DeserializeAsync<CodexRouterPackageManifest>(stream, _json, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteInstalledManifestAsync(CodexRouterPackageManifest manifest, CancellationToken cancellationToken)
    {
        var temp = _layout.InstalledManifest + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, true))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, _json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temp, _layout.InstalledManifest, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    private void EnsureNotRunningFromInstalledBin()
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current)) return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(current));
        if (string.Equals(directory?.TrimEnd(Path.DirectorySeparatorChar), _layout.BinDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Install/upgrade/uninstall must be run from the standalone installer, not from an executable inside the installed bin directory.");
        }
    }

    private void EnsureInstalledProcessesStopped()
    {
        var targetBin = Path.GetFullPath(_layout.BinDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var names = new[] { "codex-route", "CodexRouterOverlay" };
        var candidates = names.SelectMany(Process.GetProcessesByName).ToArray();
        var running = new List<Process>();
        try
        {
            foreach (var process in candidates)
            {
                string? executablePath = null;
                try { executablePath = process.MainModule?.FileName; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }

                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    continue;
                }

                var processDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath))?
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(processDirectory, targetBin, StringComparison.OrdinalIgnoreCase))
                {
                    running.Add(process);
                }
            }

            if (running.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Stop Codex Router processes from '{_layout.BinDirectory}' before install/upgrade/uninstall. Running PIDs: {string.Join(", ", running.Select(static p => p.Id))}");
            }
        }
        finally
        {
            foreach (var process in candidates) process.Dispose();
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
