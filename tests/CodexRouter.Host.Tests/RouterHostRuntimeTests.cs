using CodexRouter.Domain;
using CodexRouter.Host;
using CodexRouter.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexRouter.Host.Tests;

public sealed class RouterHostRuntimeTests
{
    [Fact]
    public async Task No_account_profiles_falls_back_to_real_codex_before_front_protocol_starts()
    {
        var root = NewRoot();
        try
        {
            var native = new FakeNativeRunner();
            var runtime = new RouterHostRuntime(new RouterPaths(root), nativeRunner: native);

            var exit = await runtime.RunAppServerAsync(
                new[] { "app-server" },
                new StringReader(string.Empty),
                new StringWriter());

            Assert.Equal(77, exit);
            Assert.Single(native.Calls);
            Assert.Equal(new[] { "app-server" }, native.Calls[0]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Unsupported_app_server_arguments_bypass_router_completely()
    {
        var root = NewRoot();
        try
        {
            var native = new FakeNativeRunner();
            var runtime = new RouterHostRuntime(new RouterPaths(root), nativeRunner: native);

            var exit = await runtime.RunAppServerAsync(
                new[] { "app-server", "--future-transport" },
                new StringReader(string.Empty),
                new StringWriter());

            Assert.Equal(77, exit);
            Assert.Single(native.Calls);
            Assert.Equal(new[] { "app-server", "--future-transport" }, native.Calls[0]);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Local_codex_schema_probe_is_compatible()
    {
        var report = await new HostCompatibilityProbe().ProbeAsync();

        Assert.Equal(CompatibilityState.Compatible, report.State);
        Assert.NotNull(report.Binary);
        Assert.True(File.Exists(report.Binary!.Path));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task With_account_profile_router_owns_stdio_handshake_instead_of_native_fallback()
    {
        var root = NewRoot();
        try
        {
            var paths = new RouterPaths(root);
            var database = new StorageDatabase(new StorageOptions(paths.DatabasePath));
            await database.InitializeAsync();
            var repository = new RouterRepository(database);
            var home = Path.Combine(root, "profiles", "a", "codex-home");
            Directory.CreateDirectory(home);
            await File.WriteAllTextAsync(Path.Combine(home, "config.toml"), "cli_auth_credentials_store = \"keyring\"\n");
            await repository.CreateAccountAsync(new AccountProfile(new AccountId("a"), "A", home));

            var native = new FakeNativeRunner();
            var runtime = new RouterHostRuntime(paths, nativeRunner: native);
            var input = new StringReader(
                "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"host-test\",\"version\":\"1\"},\"capabilities\":{\"experimentalApi\":false}}}\n" +
                "{\"method\":\"initialized\"}\n");
            var output = new StringWriter();

            var exit = await runtime.RunAppServerAsync(new[] { "app-server" }, input, output);

            Assert.Equal(0, exit);
            Assert.Empty(native.Calls);
            Assert.Contains("\"userAgent\":\"codex-router/0.1.0\"", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Desktop_global_options_and_analytics_flag_route_to_router_stdio()
    {
        var root = NewRoot();
        try
        {
            var paths = new RouterPaths(root);
            var database = new StorageDatabase(new StorageOptions(paths.DatabasePath));
            await database.InitializeAsync();
            var repository = new RouterRepository(database);
            var home = Path.Combine(root, "profiles", "desktop", "codex-home");
            Directory.CreateDirectory(home);
            await File.WriteAllTextAsync(Path.Combine(home, "config.toml"), "cli_auth_credentials_store = \"keyring\"\n");
            await repository.CreateAccountAsync(new AccountProfile(new AccountId("desktop"), "Desktop", home));

            var native = new FakeNativeRunner();
            var runtime = new RouterHostRuntime(paths, nativeRunner: native);
            var input = new StringReader(
                "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"desktop-test\",\"version\":\"1\"},\"capabilities\":{\"experimentalApi\":false}}}\n" +
                "{\"method\":\"initialized\"}\n");
            var output = new StringWriter();

            var exit = await runtime.RunAppServerAsync(
                new[] { "-c", "features.code_mode_host=true", "app-server", "--analytics-default-enabled" },
                input,
                output);

            Assert.Equal(0, exit);
            Assert.Empty(native.Calls);
            Assert.Contains("\"userAgent\":\"codex-router/0.1.0\"", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task App_server_non_stdio_transport_falls_back_with_original_arguments()
    {
        var root = NewRoot();
        try
        {
            var native = new FakeNativeRunner();
            var runtime = new RouterHostRuntime(new RouterPaths(root), nativeRunner: native);
            var arguments = new[] { "-c", "features.code_mode_host=true", "app-server", "--listen", "ws://127.0.0.1:4500" };

            var exit = await runtime.RunAppServerAsync(arguments, new StringReader(string.Empty), new StringWriter());

            Assert.Equal(77, exit);
            Assert.Single(native.Calls);
            Assert.Equal(arguments, native.Calls[0]);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-router-host-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeNativeRunner : INativeCodexRunner
    {
        public List<string[]> Calls { get; } = new();
        public Task<int> RunInheritedAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(arguments.ToArray());
            return Task.FromResult(77);
        }
    }
}
