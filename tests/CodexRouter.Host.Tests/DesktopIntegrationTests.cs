using CodexRouter.Host;
using Xunit;

namespace CodexRouter.Host.Tests;

public sealed class DesktopIntegrationTests
{
    [Fact]
    public async Task Enable_and_disable_restore_exact_previous_value()
    {
        var root = NewRoot();
        try
        {
            var shim = Path.Combine(root, "codex-route.exe");
            await File.WriteAllTextAsync(shim, "fake");
            var environment = new FakeEnvironment();
            environment.Set(CodexDesktopIntegrationManager.CodexCliPathVariable, @"C:\custom\codex-old.exe");
            var manager = new CodexDesktopIntegrationManager(new RouterPaths(root), environment);

            var enabled = await manager.EnableAsync(shim, forceReplaceExisting: true);
            Assert.True(enabled.Changed);
            Assert.Equal(Path.GetFullPath(shim), environment.Get(CodexDesktopIntegrationManager.CodexCliPathVariable));
            Assert.True(File.Exists(Path.Combine(root, "integration-state.json")));

            var disabled = await manager.DisableAsync();
            Assert.True(disabled.Changed);
            Assert.Equal(@"C:\custom\codex-old.exe", environment.Get(CodexDesktopIntegrationManager.CodexCliPathVariable));
            Assert.False(File.Exists(Path.Combine(root, "integration-state.json")));
            Assert.Equal(2, environment.BroadcastCount);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Existing_external_redirect_is_never_overwritten_without_force()
    {
        var root = NewRoot();
        try
        {
            var shim = Path.Combine(root, "codex-route.exe");
            await File.WriteAllTextAsync(shim, "fake");
            var environment = new FakeEnvironment();
            environment.Set(CodexDesktopIntegrationManager.CodexCliPathVariable, @"C:\other\router.exe");
            var manager = new CodexDesktopIntegrationManager(new RouterPaths(root), environment);

            var result = await manager.EnableAsync(shim);

            Assert.False(result.Changed);
            Assert.Equal(DesktopIntegrationStatus.RedirectedElsewhere, result.Status);
            Assert.Equal(@"C:\other\router.exe", environment.Get(CodexDesktopIntegrationManager.CodexCliPathVariable));
            Assert.False(File.Exists(Path.Combine(root, "integration-state.json")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Disable_refuses_to_clobber_external_change_after_router_enable()
    {
        var root = NewRoot();
        try
        {
            var shim = Path.Combine(root, "codex-route.exe");
            await File.WriteAllTextAsync(shim, "fake");
            var environment = new FakeEnvironment();
            var manager = new CodexDesktopIntegrationManager(new RouterPaths(root), environment);
            _ = await manager.EnableAsync(shim);

            environment.Set(CodexDesktopIntegrationManager.CodexCliPathVariable, @"C:\external\changed.exe");
            var result = await manager.DisableAsync();

            Assert.False(result.Changed);
            Assert.Equal(DesktopIntegrationStatus.Conflict, result.Status);
            Assert.Equal(@"C:\external\changed.exe", environment.Get(CodexDesktopIntegrationManager.CodexCliPathVariable));
            Assert.True(File.Exists(Path.Combine(root, "integration-state.json")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task No_previous_value_is_deleted_on_disable_not_replaced_with_empty_string()
    {
        var root = NewRoot();
        try
        {
            var shim = Path.Combine(root, "codex-route.exe");
            await File.WriteAllTextAsync(shim, "fake");
            var environment = new FakeEnvironment();
            var manager = new CodexDesktopIntegrationManager(new RouterPaths(root), environment);

            _ = await manager.EnableAsync(shim);
            _ = await manager.DisableAsync();

            Assert.False(environment.Exists(CodexDesktopIntegrationManager.CodexCliPathVariable));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Missing_shim_is_rejected_before_environment_change()
    {
        var root = NewRoot();
        try
        {
            var environment = new FakeEnvironment();
            var manager = new CodexDesktopIntegrationManager(new RouterPaths(root), environment);

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                manager.EnableAsync(Path.Combine(root, "missing.exe")));
            Assert.False(environment.Exists(CodexDesktopIntegrationManager.CodexCliPathVariable));
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-router-host-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeEnvironment : IUserEnvironmentStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public int BroadcastCount { get; private set; }
        public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;
        public bool Exists(string name) => _values.ContainsKey(name);
        public void Set(string name, string value) => _values[name] = value;
        public void Delete(string name) => _values.Remove(name);
        public void BroadcastChanged() => BroadcastCount++;
    }
}
