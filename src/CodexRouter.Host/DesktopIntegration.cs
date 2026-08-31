using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexRouter.Host;

public enum DesktopIntegrationStatus
{
    NotConfigured,
    Active,
    RedirectedElsewhere,
    ShimMissing,
    StateMissing,
    Conflict
}

public sealed record DesktopIntegrationProbe(
    DesktopIntegrationStatus Status,
    string? CurrentCodexCliPath,
    string ShimPath,
    bool ShimExists,
    bool StateExists,
    string? Message);

public sealed record DesktopIntegrationState(
    string ShimPath,
    bool OriginalValueExisted,
    string? OriginalValue,
    DateTimeOffset InstalledAt,
    string Version = "1");

public sealed record DesktopIntegrationChangeResult(
    DesktopIntegrationStatus Status,
    bool Changed,
    string Message);

public interface IUserEnvironmentStore
{
    string? Get(string name);
    bool Exists(string name);
    void Set(string name, string value);
    void Delete(string name);
    void BroadcastChanged();
}

[SupportedOSPlatform("windows")]
public sealed class WindowsUserEnvironmentStore : IUserEnvironmentStore
{
    public string? Get(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: false);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public bool Exists(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: false);
        return key?.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) == true;
    }

    public void Set(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey("Environment", writable: true)
            ?? throw new InvalidOperationException("Could not open HKCU\\Environment for writing.");
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void BroadcastChanged()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var environment = Marshal.StringToHGlobalUni("Environment");
        try
        {
            _ = SendMessageTimeout(
                new IntPtr(0xffff),
                0x001A,
                IntPtr.Zero,
                environment,
                0x0002,
                5000,
                out _);
        }
        finally
        {
            Marshal.FreeHGlobal(environment);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);
}

public sealed class CodexDesktopIntegrationManager
{
    public const string CodexCliPathVariable = "CODEX_CLI_PATH";

    private readonly RouterPaths _paths;
    private readonly IUserEnvironmentStore _environment;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public CodexDesktopIntegrationManager(RouterPaths paths, IUserEnvironmentStore? environment = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _environment = environment ?? CreateDefaultEnvironmentStore();
    }

    private static IUserEnvironmentStore CreateDefaultEnvironmentStore()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Codex Desktop integration is only supported on Windows.");
        return new WindowsUserEnvironmentStore();
    }

    public DesktopIntegrationProbe Probe(string shimPath)
    {
        shimPath = Path.GetFullPath(shimPath);
        var current = _environment.Get(CodexCliPathVariable);
        var stateExists = File.Exists(_paths.IntegrationStatePath);
        var shimExists = File.Exists(shimPath);

        if (PathEquals(current, shimPath))
        {
            return new DesktopIntegrationProbe(
                shimExists ? DesktopIntegrationStatus.Active : DesktopIntegrationStatus.ShimMissing,
                current,
                shimPath,
                shimExists,
                stateExists,
                shimExists ? "Codex Desktop redirect is active." : "CODEX_CLI_PATH points to the Router shim, but the shim file is missing.");
        }

        if (!string.IsNullOrWhiteSpace(current))
        {
            return new DesktopIntegrationProbe(
                stateExists ? DesktopIntegrationStatus.Conflict : DesktopIntegrationStatus.RedirectedElsewhere,
                current,
                shimPath,
                shimExists,
                stateExists,
                stateExists
                    ? "A Router integration state exists, but CODEX_CLI_PATH was changed externally."
                    : "CODEX_CLI_PATH is already owned by another tool or custom configuration.");
        }

        return new DesktopIntegrationProbe(
            stateExists ? DesktopIntegrationStatus.StateMissing : DesktopIntegrationStatus.NotConfigured,
            current,
            shimPath,
            shimExists,
            stateExists,
            stateExists
                ? "A previous integration state exists, but the redirect is no longer active."
                : "Codex Desktop is not redirected through Codex Router.");
    }

    public async Task<DesktopIntegrationChangeResult> EnableAsync(
        string shimPath,
        bool forceReplaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        shimPath = Path.GetFullPath(shimPath);
        if (!File.Exists(shimPath))
        {
            throw new FileNotFoundException("Router shim does not exist.", shimPath);
        }

        _paths.EnsureCreated();
        var current = _environment.Get(CodexCliPathVariable);
        var existed = _environment.Exists(CodexCliPathVariable);
        if (PathEquals(current, shimPath))
        {
            return new DesktopIntegrationChangeResult(
                DesktopIntegrationStatus.Active,
                false,
                "Codex Desktop redirect is already active.");
        }
        if (!string.IsNullOrWhiteSpace(current) && !forceReplaceExisting)
        {
            return new DesktopIntegrationChangeResult(
                DesktopIntegrationStatus.RedirectedElsewhere,
                false,
                $"Refusing to overwrite existing {CodexCliPathVariable}='{current}'.");
        }

        var state = new DesktopIntegrationState(
            shimPath,
            existed,
            current,
            DateTimeOffset.UtcNow);
        await WriteStateAtomicallyAsync(state, cancellationToken).ConfigureAwait(false);

        try
        {
            _environment.Set(CodexCliPathVariable, shimPath);
            _environment.BroadcastChanged();
            return new DesktopIntegrationChangeResult(
                DesktopIntegrationStatus.Active,
                true,
                "Codex Desktop redirect enabled. Restart Codex Desktop to apply it.");
        }
        catch
        {
            TryDeleteState();
            throw;
        }
    }

    public async Task<DesktopIntegrationChangeResult> DisableAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return new DesktopIntegrationChangeResult(
                DesktopIntegrationStatus.StateMissing,
                false,
                "No Router integration state exists, so no user environment value was changed.");
        }

        var current = _environment.Get(CodexCliPathVariable);
        if (!PathEquals(current, state.ShimPath) && !force)
        {
            return new DesktopIntegrationChangeResult(
                DesktopIntegrationStatus.Conflict,
                false,
                $"{CodexCliPathVariable} changed externally to '{current}'. Refusing to overwrite it while restoring Router state.");
        }

        if (state.OriginalValueExisted)
        {
            _environment.Set(CodexCliPathVariable, state.OriginalValue ?? string.Empty);
        }
        else
        {
            _environment.Delete(CodexCliPathVariable);
        }
        _environment.BroadcastChanged();
        TryDeleteState();

        return new DesktopIntegrationChangeResult(
            DesktopIntegrationStatus.NotConfigured,
            true,
            "Codex Desktop redirect disabled and the previous user environment value was restored.");
    }

    public async Task<DesktopIntegrationState?> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.IntegrationStatePath))
        {
            return null;
        }
        await using var stream = new FileStream(
            _paths.IntegrationStatePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            useAsync: true);
        try
        {
            return await JsonSerializer.DeserializeAsync<DesktopIntegrationState>(stream, _json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Router integration-state.json is invalid.", ex);
        }
    }

    private async Task WriteStateAtomicallyAsync(DesktopIntegrationState state, CancellationToken cancellationToken)
    {
        var destination = _paths.IntegrationStatePath;
        var temp = destination + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, state, _json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    private void TryDeleteState()
    {
        try
        {
            if (File.Exists(_paths.IntegrationStatePath))
            {
                File.Delete(_paths.IntegrationStatePath);
            }
        }
        catch (IOException)
        {
            // Recovery/diagnostics can surface the stale state file later.
        }
    }

    private static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }
        try
        {
            return string.Equals(Path.GetFullPath(left.Trim('"')), Path.GetFullPath(right.Trim('"')), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
