using CodexRouter.Protocol;

namespace CodexRouter.Host;

/// <summary>
/// Resolves the real Codex CLI from inside Codex Router without ever treating the
/// Router shim in CODEX_CLI_PATH as the native Codex binary.
/// </summary>
public sealed class NativeCodexLocator
{
    private readonly RouterPaths _paths;
    private readonly CodexBinaryDiscovery _discovery;
    private readonly IUserEnvironmentStore _environment;
    private readonly CodexDesktopIntegrationManager _integration;
    private readonly CodexBinaryDiscoveryOptions _fallbackOptions;

    public NativeCodexLocator(
        RouterPaths? paths = null,
        CodexBinaryDiscovery? discovery = null,
        IUserEnvironmentStore? environment = null,
        CodexBinaryDiscoveryOptions? fallbackOptions = null)
    {
        _paths = paths ?? RouterPaths.Default;
        _discovery = discovery ?? new CodexBinaryDiscovery();
        _environment = environment ?? CreateDefaultEnvironmentStore();
        _integration = new CodexDesktopIntegrationManager(_paths, _environment);
        _fallbackOptions = fallbackOptions ?? new CodexBinaryDiscoveryOptions();
    }

    public async Task<BinaryDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configured = _environment.Get(CodexDesktopIntegrationManager.CodexCliPathVariable);
        DesktopIntegrationState? state = null;
        try
        {
            state = await _integration.ReadStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            // A broken state file must not make us execute the Router shim recursively.
            // Recovery will surface the stale/corrupt state separately.
        }

        if (state is not null && PathEquals(configured, state.ShimPath))
        {
            if (state.OriginalValueExisted && !string.IsNullOrWhiteSpace(state.OriginalValue))
            {
                return await _discovery.DiscoverAsync(
                    new CodexBinaryDiscoveryOptions(ExplicitPath: state.OriginalValue),
                    cancellationToken).ConfigureAwait(false);
            }

            return await DiscoverWithoutRouterRedirectAsync(cancellationToken).ConfigureAwait(false);
        }

        // Fail closed against recursion even if integration-state.json was deleted or
        // corrupted while CODEX_CLI_PATH still points at our canonical shim.
        if (LooksLikeRouterShim(configured) || PathEquals(configured, Environment.ProcessPath))
        {
            return await DiscoverWithoutRouterRedirectAsync(cancellationToken).ConfigureAwait(false);
        }

        // Read the current-user setting directly rather than trusting the current
        // process environment, which may be stale after WM_SETTINGCHANGE.
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return await _discovery.DiscoverAsync(
                new CodexBinaryDiscoveryOptions(ExplicitPath: configured),
                cancellationToken).ConfigureAwait(false);
        }

        return await DiscoverWithoutRouterRedirectAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<BinaryDiscoveryResult> DiscoverWithoutRouterRedirectAsync(CancellationToken cancellationToken) =>
        _discovery.DiscoverAsync(
            _fallbackOptions with { ExplicitPath = null, IgnoreCodexCliPath = true },
            cancellationToken);

    private static IUserEnvironmentStore CreateDefaultEnvironmentStore()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native Codex resolution for Codex Desktop is Windows-only.");
        return new WindowsUserEnvironmentStore();
    }

    private static bool LooksLikeRouterShim(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return string.Equals(
                Path.GetFileName(Path.GetFullPath(path.Trim().Trim('"'))),
                "codex-route.exe",
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(left.Trim().Trim('"')),
                Path.GetFullPath(right.Trim().Trim('"')),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
