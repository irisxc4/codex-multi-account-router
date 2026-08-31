using CodexRouter.Host;
using CodexRouter.Protocol;
using Xunit;

namespace CodexRouter.Host.Tests;

public sealed class NativeCodexLocatorTests
{
    [Fact]
    public async Task Active_router_redirect_preserves_original_custom_codex_path()
    {
        var root = NewRoot();
        try
        {
            var original = Path.Combine(root, "custom-codex.exe");
            var shim = Path.Combine(root, "codex-route.exe");
            await File.WriteAllTextAsync(original, "native");
            await File.WriteAllTextAsync(shim, "router");

            var environment = new FakeEnvironment();
            environment.Set(CodexDesktopIntegrationManager.CodexCliPathVariable, original);
            var paths = new RouterPaths(root);
            var integration = new CodexDesktopIntegrationManager(paths, environment);
            _ = await integration.EnableAsync(shim, forceReplaceExisting: true);

            var runner = new FakeRunner();
            var locator = new NativeCodexLocator(
                paths,
                new CodexBinaryDiscovery(runner),
                environment,
                new CodexBinaryDiscoveryOptions(PathEnvironmentOverride: string.Empty));

            var result = await locator.DiscoverAsync();

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(Path.GetFullPath(original), result.Binary!.Path);
            Assert.All(runner.Requests, request => Assert.Equal(Path.GetFullPath(original), Path.GetFullPath(request.FileName)));
            Assert.DoesNotContain(runner.Requests, request =>
                string.Equals(Path.GetFullPath(request.FileName), Path.GetFullPath(shim), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Active_router_redirect_without_previous_override_skips_shim_and_discovers_desktop_codex()
    {
        var root = NewRoot();
        try
        {
            var shim = Path.Combine(root, "codex-route.exe");
            await File.WriteAllTextAsync(shim, "router");
            var localAppData = Path.Combine(root, "localappdata");
            var native = Path.Combine(localAppData, "OpenAI", "Codex", "bin", "build-a", "codex.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(native)!);
            await File.WriteAllTextAsync(native, "native");

            var environment = new FakeEnvironment();
            var paths = new RouterPaths(root);
            var integration = new CodexDesktopIntegrationManager(paths, environment);
            _ = await integration.EnableAsync(shim);

            var runner = new FakeRunner();
            var locator = new NativeCodexLocator(
                paths,
                new CodexBinaryDiscovery(runner),
                environment,
                new CodexBinaryDiscoveryOptions(
                    LocalAppDataOverride: localAppData,
                    PathEnvironmentOverride: string.Empty));

            var result = await locator.DiscoverAsync();

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(Path.GetFullPath(native), result.Binary!.Path);
            Assert.DoesNotContain(result.Attempts, attempt =>
                string.Equals(Path.GetFullPath(attempt.Path), Path.GetFullPath(shim), StringComparison.OrdinalIgnoreCase));
            Assert.All(runner.Requests, request => Assert.Equal(Path.GetFullPath(native), Path.GetFullPath(request.FileName)));
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-router-native-locator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = new();

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new ProcessResult(0, "codex-cli 0.test", string.Empty, false));
        }
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
