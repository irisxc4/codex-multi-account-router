using CodexRouter.Domain;
using CodexRouter.Host;
using CodexRouter.Maintenance;
using Xunit;

namespace CodexRouter.Maintenance.Tests;

public sealed class MaintenanceTests
{
    [Fact]
    public async Task Package_builder_and_verifier_round_trip_and_detect_tampering()
    {
        var root = NewRoot();
        try
        {
            var cli = Path.Combine(root, "cli");
            var overlay = Path.Combine(root, "overlay");
            var package = Path.Combine(root, "package");
            Directory.CreateDirectory(cli);
            Directory.CreateDirectory(overlay);
            await File.WriteAllTextAsync(Path.Combine(cli, "codex-route.exe"), "route-v1");
            await File.WriteAllTextAsync(Path.Combine(overlay, "CodexRouterOverlay.exe"), "overlay-v1");

            var manifest = await new PackageBuilder().BuildAsync(cli, overlay, package, "1.2.3");
            var verified = await new PackageVerifier().VerifyAsync(package);

            Assert.Equal("1.2.3", manifest.Version);
            Assert.Equal(manifest.Version, verified.Version);
            Assert.Equal(manifest.Architecture, verified.Architecture);
            Assert.Equal(manifest.BuiltAt, verified.BuiltAt);
            Assert.Equal(manifest.Files.OrderBy(file => file.Name), verified.Files.OrderBy(file => file.Name));
            Assert.Equal(2, verified.Files.Count);

            await File.AppendAllTextAsync(Path.Combine(package, "codex-route.exe"), "tampered");
            await Assert.ThrowsAsync<InvalidDataException>(() => new PackageVerifier().VerifyAsync(package));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Recovery_reinstalls_missing_binaries_from_verified_package_without_enabling_redirect()
    {
        var sandbox = NewRoot();
        try
        {
            var package = await BuildFakePackageAsync(sandbox);
            var installRoot = Path.Combine(sandbox, "installed");
            var startup = new FakeStartup();
            var environment = new FakeEnvironment();
            var integration = new CodexDesktopIntegrationManager(new RouterPaths(installRoot), environment);
            var recovery = new RecoveryService(
                installRoot,
                startup,
                integration,
                compatibilityProbe: _ => Task.FromResult(CompatibleReport()));

            var report = await recovery.RepairAsync(new RecoveryOptions(PackageDirectory: package));

            Assert.True(File.Exists(Path.Combine(installRoot, "bin", "codex-route.exe")));
            Assert.True(File.Exists(Path.Combine(installRoot, "bin", "CodexRouterOverlay.exe")));
            Assert.True(report.Repaired >= 1);
            Assert.False(environment.Exists(CodexDesktopIntegrationManager.CodexCliPathVariable));
            Assert.Contains(report.Items, item => item.Kind == "binary" && item.Status == "repaired");
        }
        finally
        {
            Cleanup(sandbox);
        }
    }

    [Fact]
    public async Task Corrupt_database_is_never_recreated_without_opt_in_and_is_quarantined_with_opt_in()
    {
        var root = NewRoot();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "router.db"), Enumerable.Repeat((byte)0xA5, 4096).ToArray());
            var startup = new FakeStartup();
            var environment = new FakeEnvironment();
            var integration = new CodexDesktopIntegrationManager(new RouterPaths(root), environment);
            var recovery = new RecoveryService(root, startup, integration, _ => Task.FromResult(CompatibleReport()));

            var safe = await recovery.RepairAsync(new RecoveryOptions(ReinstallMissingBinaries: false));
            Assert.Contains(safe.Items, item => item.Kind == "storage" && item.Status == "conflict");
            Assert.True(File.Exists(Path.Combine(root, "router.db")));

            var destructive = await recovery.RepairAsync(new RecoveryOptions(
                ReinstallMissingBinaries: false,
                RecreateCorruptDatabase: true));
            Assert.Contains(destructive.Items, item => item.Kind == "storage" && item.Status == "repaired");
            Assert.True(File.Exists(Path.Combine(root, "router.db")));
            Assert.NotEmpty(Directory.EnumerateFiles(root, "router.db.corrupt-*"));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Diagnostic_redactor_removes_credentials_emails_and_long_secret_like_values()
    {
        var redactor = new DiagnosticRedactor();
        var longSecret = new string('A', 64);
        var input = $$"""
            Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789
            {"access_token":"{{longSecret}}","refresh_token":"refresh-secret-value-1234567890"}
            user@example.test
            {{longSecret}}
            """;

        var output = redactor.Redact(input);

        Assert.DoesNotContain("user@example.test", output, StringComparison.Ordinal);
        Assert.DoesNotContain(longSecret, output, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz0123456789", output, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", output, StringComparison.Ordinal);
        Assert.Contains("[EMAIL]", output, StringComparison.Ordinal);
    }

    private static async Task<string> BuildFakePackageAsync(string root)
    {
        var cli = Path.Combine(root, "publish-cli");
        var overlay = Path.Combine(root, "publish-overlay");
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(cli);
        Directory.CreateDirectory(overlay);
        await File.WriteAllTextAsync(Path.Combine(cli, "codex-route.exe"), "route");
        await File.WriteAllTextAsync(Path.Combine(overlay, "CodexRouterOverlay.exe"), "overlay");
        _ = await new PackageBuilder().BuildAsync(cli, overlay, package, "test");
        return package;
    }

    private static CompatibilityReport CompatibleReport() =>
        new(
            CompatibilityState.Compatible,
            new BinaryIdentity(
                Path.Combine(Path.GetTempPath(), "codex.exe"),
                "codex-cli test",
                new string('a', 64),
                1,
                DateTimeOffset.UtcNow),
            null,
            DateTimeOffset.UtcNow,
            Array.Empty<CompatibilityIssue>(),
            Array.Empty<string>(),
            Array.Empty<string>());

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-router-maintenance-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeStartup : IStartupRegistration
    {
        private string? _path;
        public bool IsEnabled(string executablePath) =>
            _path is not null && string.Equals(Path.GetFullPath(executablePath), _path, StringComparison.OrdinalIgnoreCase);
        public void Enable(string executablePath) => _path = Path.GetFullPath(executablePath);
        public void Disable() => _path = null;
    }

    private sealed class FakeEnvironment : IUserEnvironmentStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;
        public bool Exists(string name) => _values.ContainsKey(name);
        public void Set(string name, string value) => _values[name] = value;
        public void Delete(string name) => _values.Remove(name);
        public void BroadcastChanged() { }
    }
}
