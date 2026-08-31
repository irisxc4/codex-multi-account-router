using System.Text.Json;
using CodexRouter.Host;
using CodexRouter.Storage;

namespace CodexRouter.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            if (RouterHostRuntime.IsAppServerInvocation(args))
            {
                using var stdio = AppServerStdio.FromConsole();
                return await new RouterHostRuntime().RunAppServerAsync(
                    args,
                    stdio.Input,
                    stdio.Output,
                    shutdown.Token).ConfigureAwait(false);
            }

            if (args.Length > 0 && string.Equals(args[0], "routerctl", StringComparison.OrdinalIgnoreCase))
            {
                return await RunRouterControlAsync(args.Skip(1).ToArray(), shutdown.Token).ConfigureAwait(false);
            }

            return await new RealCodexProcess().RunInheritedAsync(args, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"codex-route: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunRouterControlAsync(string[] args, CancellationToken cancellationToken)
    {
        var command = args.Length == 0 ? "status" : args[0].ToLowerInvariant();
        var paths = RouterPaths.Default;
        paths.EnsureCreated();
        var integration = new CodexDesktopIntegrationManager(paths);
        var shimPath = ResolvePublishedShimPath(args);

        switch (command)
        {
            case "status":
            {
                var database = new StorageDatabase(new StorageOptions(paths.DatabasePath));
                var accountCount = 0;
                try
                {
                    await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    accountCount = (await new RouterRepository(database).ListAccountsAsync(cancellationToken).ConfigureAwait(false)).Count;
                }
                catch
                {
                    // Status still reports integration even if storage needs recovery.
                }
                var probe = integration.Probe(shimPath);
                WriteJson(new
                {
                    integration = probe,
                    accountCount,
                    dataRoot = paths.Root
                });
                return 0;
            }

            case "enable":
            {
                var database = new StorageDatabase(new StorageOptions(paths.DatabasePath));
                await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
                var repository = new RouterRepository(database);
                var accounts = await repository.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
                if (accounts.Count == 0)
                {
                    Console.Error.WriteLine("Refusing to enable Desktop redirect before at least one Router account profile exists.");
                    return 2;
                }
                var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
                var result = await integration.EnableAsync(shimPath, force, cancellationToken).ConfigureAwait(false);
                WriteJson(result);
                return result.Status == DesktopIntegrationStatus.Active ? 0 : 3;
            }

            case "disable":
            {
                var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
                var result = await integration.DisableAsync(force, cancellationToken).ConfigureAwait(false);
                WriteJson(result);
                return result.Status is DesktopIntegrationStatus.NotConfigured or DesktopIntegrationStatus.StateMissing ? 0 : 3;
            }

            case "doctor":
            {
                var desktop = await new CodexDesktopDiscovery().DiscoverAsync(cancellationToken).ConfigureAwait(false);
                var compatibility = await new HostCompatibilityProbe().ProbeAsync(cancellationToken).ConfigureAwait(false);
                var integrationProbe = integration.Probe(shimPath);
                WriteJson(new
                {
                    desktop,
                    compatibilityState = compatibility.State,
                    binary = compatibility.Binary,
                    integration = integrationProbe,
                    dataRoot = paths.Root
                });
                return compatibility.State is CodexRouter.Domain.CompatibilityState.Compatible or CodexRouter.Domain.CompatibilityState.Degraded ? 0 : 4;
            }

            default:
                Console.Error.WriteLine("Usage: codex-route routerctl [status|enable|disable|doctor] [--force] [--shim <path>]");
                return 64;
        }
    }

    private static string ResolvePublishedShimPath(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], "--shim", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("Could not determine the codex-route executable path. Supply --shim <path>.");
        }
        return Path.GetFullPath(processPath);
    }

    private static void WriteJson<T>(T value) => Console.Out.WriteLine(JsonSerializer.Serialize(value, Json));
}
