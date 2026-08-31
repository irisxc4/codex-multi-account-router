using System.Text;
using System.Text.Json;
using CodexRouter.Control;
using CodexRouter.Domain;
using CodexRouter.Routing;
using CodexRouter.Rpc;
using CodexRouter.Storage;
using CodexRouter.Workers;

namespace CodexRouter.Host;

public sealed record RouterHostStartupResult(
    bool RouterActive,
    bool FellBackToNative,
    string Reason,
    CompatibilityState? Compatibility = null);

public sealed class RouterHostRuntime
{
    private static readonly HashSet<string> GlobalOptionsWithValues = new(StringComparer.Ordinal)
    {
        "-c",
        "--config"
    };

    private readonly RouterPaths _paths;
    private readonly HostCompatibilityProbe _compatibilityProbe;
    private readonly INativeCodexRunner _nativeRunner;

    public RouterHostRuntime(
        RouterPaths? paths = null,
        HostCompatibilityProbe? compatibilityProbe = null,
        INativeCodexRunner? nativeRunner = null)
    {
        _paths = paths ?? RouterPaths.Default;
        var nativeLocator = new NativeCodexLocator(_paths);
        _compatibilityProbe = compatibilityProbe ?? new HostCompatibilityProbe(nativeLocator: nativeLocator);
        _nativeRunner = nativeRunner ?? new RealCodexProcess(nativeLocator: nativeLocator);
    }

    /// <summary>
    /// Returns true when the original CLI invocation targets the app-server command,
    /// allowing global options before that command. This is intentionally separate
    /// from the Router's stdio compatibility check so unsupported transports still
    /// reach <see cref="RunAppServerAsync"/> and can be passed through unchanged.
    /// </summary>
    public static bool IsAppServerInvocation(IReadOnlyList<string> originalArguments)
    {
        ArgumentNullException.ThrowIfNull(originalArguments);
        return TryFindAppServerCommand(originalArguments, out _);
    }

    public async Task<int> RunAppServerAsync(
        IReadOnlyList<string> originalArguments,
        TextReader frontInput,
        TextWriter frontOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalArguments);
        ArgumentNullException.ThrowIfNull(frontInput);
        ArgumentNullException.ThrowIfNull(frontOutput);

        // The Router implements only the stdio app-server form. Any future transport
        // flags are safer on the real CLI until compatibility support is explicit.
        if (!TryFindAppServerCommand(originalArguments, out var appServerIndex) ||
            !IsRouterCompatibleStdioInvocation(originalArguments, appServerIndex))
        {
            return await _nativeRunner.RunInheritedAsync(originalArguments, cancellationToken).ConfigureAwait(false);
        }

        StorageDatabase? database = null;
        RouterRepository? repository = null;
        CompatibilityReport? compatibility = null;
        try
        {
            _paths.EnsureCreated();
            database = new StorageDatabase(new StorageOptions(_paths.DatabasePath));
            await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            repository = new RouterRepository(database);

            compatibility = await _compatibilityProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            await repository.AppendCompatibilityRunAsync(compatibility, cancellationToken).ConfigureAwait(false);
            if (compatibility.State is not (CompatibilityState.Compatible or CompatibilityState.Degraded) || compatibility.Binary is null)
            {
                await WriteHostLogAsync(
                    "startup-fallback",
                    $"compatibility={compatibility.State}",
                    CancellationToken.None).ConfigureAwait(false);
                return await _nativeRunner.RunInheritedAsync(originalArguments, cancellationToken).ConfigureAwait(false);
            }

            var accounts = await repository.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
            if (accounts.Count == 0)
            {
                await WriteHostLogAsync("startup-fallback", "no managed account profiles", CancellationToken.None)
                    .ConfigureAwait(false);
                return await _nativeRunner.RunInheritedAsync(originalArguments, cancellationToken).ConfigureAwait(false);
            }

            // Existing profiles created before root hooks.json became a managed
            // asset may be missing hooks. Repair is deliberately fail-open: a
            // broken global template must never block Codex startup or login.
            await SynchronizeMissingHooksFailOpenAsync(accounts, cancellationToken).ConfigureAwait(false);

            var clientContext = new WorkerClientContext();
            var workerFactory = new CodexAppServerWorkerFactory(
                compatibility.Binary.Path,
                new WorkerStartOptions(),
                clientContext);
            await using var workerPool = new WorkerPool(workerFactory);
            var materializer = new ProfileMaterializer(new ProfileLayout(_paths.Root));
            var desktopDiscovery = await new CodexDesktopDiscovery().DiscoverAsync(cancellationToken).ConfigureAwait(false);
            var desktopExecutable = desktopDiscovery.RunningProcesses
                .FirstOrDefault(static process => process.HasMainWindow && !string.IsNullOrWhiteSpace(process.ExecutablePath))
                ?.ExecutablePath;
            await using var controlServer = new RouterControlServer(
                _paths.Root,
                repository,
                workerPool,
                materializer,
                nativeCodexExecutable: compatibility.Binary.Path,
                desktopExecutable: desktopExecutable);
            await controlServer.StartAsync(cancellationToken).ConfigureAwait(false);
            var router = new RouterCoordinator(repository, workerPool);
            await using var multiplexer = new RpcMultiplexer(
                repository,
                workerPool,
                router,
                clientContext,
                options: new RpcMultiplexerOptions());

            await multiplexer.RunAsync(frontInput, frontOutput, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Startup faults can fall back safely because no Router response has been
            // intentionally emitted yet. Per-request faults are handled inside the RPC
            // multiplexer and do not escape here under normal operation.
            await WriteHostLogAsync("startup-error", ex.ToString(), CancellationToken.None).ConfigureAwait(false);
            if (repository is null || compatibility is null)
            {
                return await _nativeRunner.RunInheritedAsync(originalArguments, cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
    }

    private async Task WriteHostLogAsync(string category, string message, CancellationToken cancellationToken)
    {
        try
        {
            _paths.EnsureCreated();
            var path = Path.Combine(_paths.LogsRoot, $"host-{DateTimeOffset.UtcNow:yyyyMMdd}.jsonl");
            var payload = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow,
                category,
                message = DiagnosticRedaction.Redact(message)
            });
            await File.AppendAllTextAsync(path, payload + Environment.NewLine, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Never write host diagnostics to stdout; stdout belongs to app-server JSONL.
        }
    }

    private async Task SynchronizeMissingHooksFailOpenAsync(
        IReadOnlyList<StoredAccount> accounts,
        CancellationToken cancellationToken)
    {
        string source;
        ProfileMaterializer materializer;
        try
        {
            source = ResolveDefaultCodexHome();
            materializer = new ProfileMaterializer(new ProfileLayout(_paths.Root));
        }
        catch (Exception ex)
        {
            await WriteHostLogAsync("hooks-sync-failed", $"source initialization failed: {ex}", CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        foreach (var account in accounts)
        {
            try
            {
                await materializer.SynchronizeMissingHooksAsync(
                        account.Profile.CodexHome,
                        source,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await WriteHostLogAsync(
                        "hooks-sync-failed",
                        $"account={account.Profile.Id.Value}; source={source}; {ex}",
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private static string ResolveDefaultCodexHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".codex");
    }

    private static bool TryFindAppServerCommand(IReadOnlyList<string> arguments, out int commandIndex)
    {
        commandIndex = -1;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.Equals(argument, "app-server", StringComparison.Ordinal))
            {
                commandIndex = index;
                return true;
            }

            // A literal separator ends global-option parsing. Anything after it is
            // positional data, not a command that Router should intercept.
            if (string.Equals(argument, "--", StringComparison.Ordinal))
            {
                return false;
            }

            // The first positional token is the subcommand. Unknown positional
            // commands must remain transparent to the native Codex CLI.
            if (string.IsNullOrEmpty(argument) || argument[0] != '-')
            {
                return false;
            }

            // Current Desktop launch uses `-c <key=value>`. Keep parsing narrow:
            // only known value-taking global options may consume the next token.
            // This prevents a future option such as `--listen app-server` from
            // accidentally being treated as the app-server command.
            var option = argument;
            var equalsIndex = option.IndexOf('=');
            if (equalsIndex >= 0)
            {
                option = option[..equalsIndex];
            }

            if (GlobalOptionsWithValues.Contains(option) && equalsIndex < 0)
            {
                if (++index >= arguments.Count)
                {
                    return false;
                }
            }
        }

        return false;
    }

    private static bool IsRouterCompatibleStdioInvocation(
        IReadOnlyList<string> arguments,
        int appServerIndex)
    {
        for (var index = appServerIndex + 1; index < arguments.Count; index++)
        {
            // This flag is emitted by current Codex Desktop and does not change
            // the JSONL stdio transport handled by Router.
            if (!string.Equals(arguments[index], "--analytics-default-enabled", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
