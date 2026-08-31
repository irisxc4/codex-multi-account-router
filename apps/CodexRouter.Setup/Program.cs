using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodexRouter.Host;
using CodexRouter.Maintenance;
using CodexRouter.Workers;

namespace CodexRouter.Setup;

internal static class Program
{
    private const uint MessageBoxOk = 0x00000000;
    private const uint MessageBoxInformation = 0x00000040;
    private const uint MessageBoxError = 0x00000010;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    public static async Task<int> Main(string[] args)
    {
        var interactive = args.Length == 0;
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            // The release artifact is a user-facing installer. A double-click supplies no
            // arguments, so make that path perform the normal install instead of flashing
            // a help window and exiting without changing anything.
            var command = interactive ? "install" : args[0].ToLowerInvariant();
            var rest = interactive ? Array.Empty<string>() : args.Skip(1).ToArray();
            var exitCode = command switch
            {
                "package" => await PackageAsync(rest, shutdown.Token).ConfigureAwait(false),
                "install" => await InstallAsync(rest, shutdown.Token).ConfigureAwait(false),
                "repair" => await RepairAsync(rest, shutdown.Token).ConfigureAwait(false),
                "diagnostics" => await DiagnosticsAsync(rest, shutdown.Token).ConfigureAwait(false),
                "compact" => await CompactAsync(rest, shutdown.Token).ConfigureAwait(false),
                "uninstall" => await UninstallAsync(rest, shutdown.Token).ConfigureAwait(false),
                "help" or "--help" or "-h" => PrintHelp(),
                _ => Unknown(command)
            };

            if (interactive && exitCode == 0)
            {
                MessageBoxW(nint.Zero, "Codex Router 安装完成。", "Codex Router 安装器", MessageBoxOk | MessageBoxInformation);
            }

            return exitCode;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            if (interactive)
            {
                MessageBoxW(nint.Zero, "Codex Router 安装已取消。", "Codex Router 安装器", MessageBoxOk | MessageBoxInformation);
            }

            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CodexRouterSetup: {ex.Message}");
            if (interactive)
            {
                MessageBoxW(
                    nint.Zero,
                    $"安装失败：{ex.Message}\n\n请完全退出 Codex 后重新运行安装器。",
                    "Codex Router 安装器",
                    MessageBoxOk | MessageBoxError);
            }

            return 1;
        }
    }

    private static async Task<int> PackageAsync(string[] args, CancellationToken cancellationToken)
    {
        var cli = RequireOption(args, "--cli");
        var overlay = RequireOption(args, "--overlay");
        var output = RequireOption(args, "--out");
        var version = RequireOption(args, "--version");
        var architecture = Option(args, "--arch") ?? "win-x64";
        var manifest = await new PackageBuilder().BuildAsync(cli, overlay, output, version, architecture, cancellationToken)
            .ConfigureAwait(false);
        WriteJson(manifest);
        return 0;
    }

    private static async Task<int> InstallAsync(string[] args, CancellationToken cancellationToken)
    {
        var package = Option(args, "--package");
        using var embeddedPackage = package is null ? EmbeddedPackageLease.Open() : null;
        package ??= embeddedPackage!.DirectoryPath;
        var root = ResolveRoot(args);
        var startup = !HasFlag(args, "--no-startup");
        var launch = !HasFlag(args, "--no-launch");
        var manager = new InstallationManager(root);
        var result = await manager.InstallAsync(package, startup, cancellationToken).ConfigureAwait(false);
        WriteJson(result);

        if (launch)
        {
            var overlay = manager.Layout.OverlayExecutable;
            if (File.Exists(overlay))
            {
                _ = Process.Start(new ProcessStartInfo(overlay)
                {
                    UseShellExecute = false,
                    WorkingDirectory = manager.Layout.BinDirectory
                });
            }
        }
        return 0;
    }

    private static async Task<int> RepairAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = ResolveRoot(args);
        var report = await new RecoveryService(root).RepairAsync(
            new RecoveryOptions(
                PackageDirectory: Option(args, "--package"),
                ReinstallMissingBinaries: !HasFlag(args, "--no-reinstall"),
                RestoreBrokenIntegration: !HasFlag(args, "--no-integration-restore"),
                RecreateCorruptDatabase: HasFlag(args, "--recreate-db"),
                ForceIntegrationRestore: HasFlag(args, "--force")),
            cancellationToken).ConfigureAwait(false);
        WriteJson(report);
        return report.Conflicts == 0 ? 0 : 4;
    }

    private static async Task<int> DiagnosticsAsync(string[] args, CancellationToken cancellationToken)
    {
        var result = await new DiagnosticsService(ResolveRoot(args)).CreateBundleAsync(cancellationToken).ConfigureAwait(false);
        WriteJson(result);
        return 0;
    }

    private static async Task<int> CompactAsync(string[] args, CancellationToken cancellationToken)
    {
        EnsureNoCodexProcesses();
        var historyText = Option(args, "--history") ?? "1";
        if (!int.TryParse(historyText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var history) || history < 0)
        {
            throw new ArgumentException("--history must be a non-negative integer.");
        }

        var materializer = new ProfileMaterializer(new ProfileLayout(ResolveRoot(args)));
        var report = await materializer.CompactTemplatesAsync(history, cancellationToken).ConfigureAwait(false);
        WriteJson(report);
        return 0;
    }

    private static async Task<int> UninstallAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = ResolveRoot(args);
        var result = await new InstallationManager(root).UninstallAsync(
            removeData: HasFlag(args, "--remove-data"),
            forceIntegrationRestore: HasFlag(args, "--force"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        WriteJson(result);
        return result.Changed || result.DataRetained ? 0 : 3;
    }

    private static string ResolveRoot(IReadOnlyList<string> args) =>
        Path.GetFullPath(Option(args, "--root") ?? RouterPaths.Default.Root);

    private static string RequireOption(IReadOnlyList<string> args, string name) =>
        Option(args, name) ?? throw new ArgumentException($"Required option {name} is missing.");

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Option {name} requires a value.");
            return args[index + 1];
        }
        return null;
    }

    private static bool HasFlag(IEnumerable<string> args, string name) =>
        args.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static void EnsureNoCodexProcesses()
    {
        var names = new[] { "ChatGPT", "Codex", "codex", "codex-route", "CodexRouterOverlay" };
        var running = names
            .SelectMany(Process.GetProcessesByName)
            .GroupBy(static process => process.Id)
            .Select(static group => group.First())
            .ToArray();
        var runningText = string.Join(", ", running.Select(static process => $"{process.ProcessName}#{process.Id}"));
        foreach (var process in running) process.Dispose();
        if (running.Length > 0)
        {
            throw new InvalidOperationException($"Refusing to compact while Codex processes are running: {runningText}");
        }
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
            CodexRouterSetup

              (no arguments) installs the embedded package and launches the Overlay
              package     --cli <publish-dir> --overlay <publish-dir> --out <dir> --version <version> [--arch win-x64]
              install     [--package <dir>] [--root <dir>] [--no-startup] [--no-launch]
              repair      [--root <dir>] [--package <dir>] [--recreate-db] [--force]
              diagnostics [--root <dir>]
              compact     [--root <dir>] [--history <n>]
              uninstall   [--root <dir>] [--remove-data] [--force]

            Install never enables CODEX_CLI_PATH automatically. Add/login at least one account first,
            then enable routing explicitly from the Overlay or `codex-route routerctl enable`.
            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'. Use 'help'.");
        return 64;
    }

    private static void WriteJson<T>(T value) => Console.Out.WriteLine(JsonSerializer.Serialize(value, Json));
}
