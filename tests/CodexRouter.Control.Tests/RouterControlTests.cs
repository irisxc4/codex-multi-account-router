using System.Text.Json;
using CodexRouter.Control;
using CodexRouter.Domain;
using CodexRouter.Storage;
using CodexRouter.Workers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexRouter.Control.Tests;

public sealed class RouterControlTests
{
    [Fact]
    public async Task Snapshot_and_mode_changes_round_trip_over_authenticated_named_pipe()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var account = await env.AddAccountAsync("a");
        await using var server = env.CreateServer();
        await server.StartAsync();
        var client = new RouterControlClient(env.Root);

        var snapshot = await client.SnapshotAsync();
        Assert.Single(snapshot.Accounts);
        Assert.Equal(account.Id.Value, snapshot.Accounts[0].Id);

        var pinned = await client.PinAsync(account.Id.Value);
        Assert.Equal("Pinned", pinned.Mode);
        Assert.Equal(account.Id.Value, pinned.PinnedAccountId);
        Assert.Equal(RouterMode.Pinned, (await env.Repository.GetRouterSettingsAsync()).Mode);

        var auto = await client.SetAutoAsync();
        Assert.Equal("Auto", auto.Mode);
        Assert.Null(auto.PinnedAccountId);
    }

    [Fact]
    public async Task Control_token_mismatch_is_rejected()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await env.AddAccountAsync("a");
        await using var server = env.CreateServer();
        await server.StartAsync();
        var endpoint = new ControlEndpoint(env.Root);
        var original = await endpoint.ReadTokenAsync();
        await File.WriteAllTextAsync(endpoint.TokenPath, new string('0', 64));
        try
        {
            var client = new RouterControlClient(env.Root);
            var error = await Assert.ThrowsAsync<RouterControlException>(() => client.SnapshotAsync());
            Assert.Equal(401, error.Code);
        }
        finally
        {
            await File.WriteAllTextAsync(endpoint.TokenPath, original);
        }
    }

    [Fact]
    public async Task Migration_job_runs_end_to_end_over_named_pipe_and_returns_explicit_target_thread()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var source = await env.AddAccountAsync("source");
        var target = await env.AddAccountAsync("target");
        await env.Repository.InsertThreadRouteAsync(new ThreadRoute(
            new ThreadId("source-thread"), source.Id, new WorkerId("source-worker"), RouteReason.Sticky,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        await using var server = env.CreateServer();
        await server.StartAsync();
        var client = new RouterControlClient(env.Root);

        var start = await client.StartMigrationAsync("source-thread", target.Id.Value);
        ControlMigrationStatus status;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            status = await client.MigrationStatusAsync(start.JobId);
            if (status.State is not ("Completed" or "Failed" or "Canceled")) await Task.Delay(20);
        } while (status.State is not ("Completed" or "Failed" or "Canceled") && DateTime.UtcNow < deadline);

        Assert.Equal("Completed", status.State);
        Assert.Equal("source-thread", status.SourceThreadId);
        Assert.Equal(source.Id.Value, status.SourceAccountId);
        Assert.Equal(target.Id.Value, status.TargetAccountId);
        Assert.StartsWith("migrated-target-", status.TargetThreadId, StringComparison.Ordinal);
        Assert.Contains("Codex Router migration job", env.Factory.Latest(target.Id).SeededText, StringComparison.Ordinal);
        Assert.Equal(source.Id, (await env.Repository.GetThreadRouteAsync(new ThreadId("source-thread")))!.AccountId);
        Assert.Equal(target.Id, (await env.Repository.GetThreadRouteAsync(new ThreadId(status.TargetThreadId!)))!.AccountId);
    }

    [Fact]
    public async Task Onboarding_login_session_survives_across_pipe_requests_and_refreshes_account()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await using var server = env.CreateServer();
        await server.StartAsync();
        var client = new RouterControlClient(env.Root);

        var start = await client.StartOnboardAsync("Fresh Account");
        Assert.NotNull(start.AuthUrl);
        Assert.Equal(ControlLoginMethods.Browser, start.LoginMethod);
        var pendingId = new AccountId(start.AccountId);
        Assert.Empty(await env.Repository.ListAccountsAsync());
        var pending = (await env.Repository.GetAccountAsync(pendingId))!;
        Assert.Equal(AccountLifecycle.Pending, pending.Lifecycle);
        var directEnvironment = ProfileWorkerNetworkRoute.LoadEnvironment(pending.Profile.CodexHome);
        Assert.NotNull(directEnvironment);
        Assert.Null(directEnvironment!["HTTP_PROXY"]);
        Assert.Null(directEnvironment["HTTPS_PROXY"]);
        env.OfficialBrowserLoginFactory.CompleteNext(success: true);

        var status = await WaitForLoginAsync(client, start.LoginId);

        Assert.Equal("completed", status.State);
        Assert.Equal("fresh@example.test", status.Email);
        var stored = await env.Repository.GetAccountAsync(new AccountId(start.AccountId));
        Assert.NotNull(stored);
        Assert.Equal("fresh@example.test", stored!.Profile.Email);
        Assert.Equal(AccountLifecycle.Active, stored.Lifecycle);
        Assert.True(stored.Profile.Enabled);
        Assert.Single(await env.Repository.ListAccountsAsync());
        Assert.NotNull(await env.Repository.GetLatestQuotaSnapshotAsync(stored.Profile.Id));
    }

    [Fact]
    public async Task Web_session_import_registers_agent_identity_activates_account_and_persists_account_proxy()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var registrar = new FakeAgentIdentityRegistrar();
        var credentialWriter = new FakeCredentialWriter();
        await using var server = env.CreateServer(registrar, credentialWriter);
        await server.StartAsync();
        var client = new RouterControlClient(env.Root);

        const string proxy = "http://127.0.0.1:7897";
        var sessionJson = CreateWebSessionJson("web-access-token-marker");
        var result = await client.ImportChatGptSessionAsync("Imported", sessionJson, proxy);

        Assert.Equal("fresh@example.test", result.Email);
        Assert.Equal("plus", result.PlanType);
        Assert.NotNull(registrar.SeenSession);
        Assert.Equal(proxy, registrar.SeenProxyUrl);
        Assert.Contains("web-access-token-marker", registrar.SeenSession!.AccessToken, StringComparison.Ordinal);
        Assert.NotNull(credentialWriter.SavedIdentity);
        Assert.Equal("runtime-imported", credentialWriter.SavedIdentity!.AgentRuntimeId);
        Assert.DoesNotContain("web-access-token-marker", CodexDirectKeyringStore.SerializeAgentIdentity(credentialWriter.SavedIdentity), StringComparison.Ordinal);

        var stored = await env.Repository.GetAccountAsync(new AccountId(result.AccountId));
        Assert.NotNull(stored);
        Assert.Equal(AccountLifecycle.Active, stored!.Lifecycle);
        Assert.True(stored.Profile.Enabled);
        Assert.Equal("Imported", stored.Profile.Alias);
        Assert.False(File.Exists(Path.Combine(stored.Profile.CodexHome, "auth.json")));
        var config = await File.ReadAllTextAsync(Path.Combine(stored.Profile.CodexHome, "config.toml"));
        Assert.Contains("cli_auth_credentials_store = \"keyring\"", config, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secret_auth_storage = true", config, StringComparison.OrdinalIgnoreCase);
        var workerEnvironment = ProfileWorkerNetworkRoute.LoadEnvironment(stored.Profile.CodexHome);
        Assert.NotNull(workerEnvironment);
        Assert.Equal(proxy, workerEnvironment!["HTTP_PROXY"]);
        Assert.Equal(proxy, workerEnvironment["HTTPS_PROXY"]);
        Assert.NotNull(await env.Repository.GetLatestQuotaSnapshotAsync(stored.Profile.Id));
    }

    [Fact]
    public async Task Web_session_import_rolls_back_pending_profile_when_agent_identity_registration_fails()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var registrar = new FakeAgentIdentityRegistrar { Failure = new InvalidOperationException("registration rejected") };
        var credentialWriter = new FakeCredentialWriter();
        await using var server = env.CreateServer(registrar, credentialWriter);
        await server.StartAsync();
        var client = new RouterControlClient(env.Root);

        var error = await Assert.ThrowsAsync<RouterControlException>(() =>
            client.ImportChatGptSessionAsync("Blocked", CreateWebSessionJson("rejected-marker"), "http://127.0.0.1:7897"));

        Assert.Contains("registration rejected", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await env.Repository.ListAllAccountsAsync());
        Assert.False(credentialWriter.SaveCalled);
        Assert.Empty(Directory.GetDirectories(Path.Combine(env.Root, "profiles")));
    }

    [Fact]
    public async Task Browser_onboarding_forwards_explicit_login_proxy_without_changing_auth_owner()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await using var server = env.CreateServer();
        await server.StartAsync();
        var client = new RouterControlClient(env.Root);

        const string proxy = "http://127.0.0.1:7897";
        var start = await client.StartOnboardAsync("Proxy Account", ControlLoginMethods.Browser, proxy);
        var invocation = await env.OfficialBrowserLoginFactory.WaitForInvocationAsync();
        Assert.Equal(proxy, invocation.ProxyUrl);
        Assert.NotNull(start.AuthUrl);
        Assert.Equal(invocation.AuthUrl.AbsoluteUri, start.AuthUrl);
        var workerEnvironment = ProfileWorkerNetworkRoute.LoadEnvironment(invocation.CodexHome);
        Assert.NotNull(workerEnvironment);
        Assert.Equal(proxy, workerEnvironment!["HTTP_PROXY"]);
        Assert.Equal(proxy, workerEnvironment["HTTPS_PROXY"]);
        env.OfficialBrowserLoginFactory.CompleteNext(success: true);

        var status = await WaitForLoginAsync(client, start.LoginId);
        Assert.Equal("completed", status.State);
    }

    [Fact]
    public async Task Device_onboarding_uses_official_app_server_typed_device_code_flow()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await using var server = env.CreateServer();
        await server.StartAsync();
        var client = new RouterControlClient(env.Root);

        const string proxy = "http://127.0.0.1:7897";
        var start = await client.StartOnboardAsync("Device Account", ControlLoginMethods.Device, proxy);
        Assert.Equal(ControlLoginMethods.Device, start.LoginMethod);
        Assert.Equal("https://chatgpt.com/device", start.AuthUrl);
        Assert.Equal("ABCD-EFGH", start.UserCode);
        var invocation = await env.OfficialBrowserLoginFactory.WaitForInvocationAsync();
        Assert.True(invocation.DeviceCode);
        Assert.Equal(proxy, invocation.ProxyUrl);
        env.OfficialBrowserLoginFactory.CompleteNext(success: true);

        var status = await WaitForLoginAsync(client, start.LoginId);
        Assert.Equal("completed", status.State);
        Assert.Equal("fresh@example.test", status.Email);
    }

    [Fact]
    public async Task Live_official_app_server_device_code_probe_is_opt_in()
    {
        var codex = Environment.GetEnvironmentVariable("CODEX_ROUTER_LIVE_CODEX");
        var required = string.Equals(Environment.GetEnvironmentVariable("CODEX_ROUTER_LIVE_REQUIRED"), "1", StringComparison.Ordinal);
        if (!required && (string.IsNullOrWhiteSpace(codex) || !File.Exists(codex))) return;
        Assert.False(string.IsNullOrWhiteSpace(codex));
        Assert.True(File.Exists(codex));

        var root = Path.Combine(Path.GetTempPath(), $"codex-router-live-device-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        IOfficialAppServerLoginSession? session = null;
        try
        {
            var profile = new AccountProfile(new AccountId("live-device-probe"), "live-device-probe", root, Enabled: false);
            session = await OfficialAppServerLoginSession.StartAsync(
                codex,
                profile,
                Environment.GetEnvironmentVariable("CODEX_ROUTER_LIVE_PROXY"),
                deviceCode: true);

            Assert.False(string.IsNullOrWhiteSpace(session.LoginId));
            Assert.False(string.IsNullOrWhiteSpace(session.UserCode));
            Assert.True(session.AuthUrl.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
                        session.AuthUrl.Host.Equals("auth.openai.com", StringComparison.OrdinalIgnoreCase) ||
                        session.AuthUrl.Host.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase));
            await session.CancelAsync();
        }
        finally
        {
            if (session is not null) await session.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Desktop_onboarding_uses_isolated_official_desktop_runner()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await using var server = env.CreateServer();
        await server.StartAsync();
        var client = new RouterControlClient(env.Root);

        const string proxy = "http://127.0.0.1:7897";
        var start = await client.StartOnboardAsync("Desktop Account", ControlLoginMethods.Desktop, proxy);
        Assert.Equal(ControlLoginMethods.Desktop, start.LoginMethod);
        var invocation = await env.DesktopLoginRunner.WaitForInvocationAsync();
        Assert.Equal(Path.Combine(env.Root, "ChatGPT.exe"), invocation.DesktopExecutable);
        Assert.EndsWith(Path.Combine(start.AccountId, "codex-home"), invocation.CodexHome, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(proxy, invocation.ProxyUrl);
        env.DesktopLoginRunner.CompleteNext(success: true);

        var status = await WaitForLoginAsync(client, start.LoginId);
        Assert.Equal("completed", status.State);
        Assert.Equal("fresh@example.test", status.Email);
    }

    [Fact]
    public async Task Canceling_native_onboarding_stops_login_and_removes_pending_profile()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await using var server = env.CreateServer();
        await server.StartAsync();
        var client = new RouterControlClient(env.Root);

        var start = await client.StartOnboardAsync("Cancel Account");
        var accountId = new AccountId(start.AccountId);
        _ = await env.OfficialBrowserLoginFactory.WaitForInvocationAsync();

        var status = await client.CancelLoginAsync(start.LoginId);

        Assert.Equal("failed", status.State);
        Assert.Contains("canceled", status.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await env.Repository.GetAccountAsync(accountId));
        Assert.False(Directory.Exists(Path.Combine(env.Root, "profiles", accountId.Value)));
    }

    [Fact]
    public async Task Failed_first_onboarding_rolls_back_created_account_profile()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var credentialWriter = new FakeCredentialWriter();
        await using var server = env.CreateServer(credentialWriter: credentialWriter);
        await server.StartAsync();
        var client = new RouterControlClient(env.Root);

        var start = await client.StartOnboardAsync("Blocked Account");
        var accountId = new AccountId(start.AccountId);
        env.OfficialBrowserLoginFactory.CompleteNext(success: false, error: "official login rejected");

        var status = await WaitForLoginAsync(client, start.LoginId);

        Assert.Equal("failed", status.State);
        Assert.True(credentialWriter.DeleteCalled);
        Assert.Null(await env.Repository.GetAccountAsync(accountId));
        Assert.False(Directory.Exists(Path.Combine(env.Root, "profiles", accountId.Value)));
    }

    [Fact]
    public async Task Failed_official_onboarding_retains_pending_profile_when_keyring_cleanup_fails()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var credentialWriter = new FakeCredentialWriter
        {
            DeleteFailure = new InvalidOperationException("keyring unavailable")
        };
        await using var server = env.CreateServer(credentialWriter: credentialWriter);
        await server.StartAsync();
        var client = new RouterControlClient(env.Root);

        var start = await client.StartOnboardAsync("Recoverable Account");
        var accountId = new AccountId(start.AccountId);
        env.OfficialBrowserLoginFactory.CompleteNext(success: false, error: "official login rejected");

        var status = await WaitForLoginAsync(client, start.LoginId);

        Assert.Equal("failed", status.State);
        Assert.Contains("keyring", status.Error, StringComparison.OrdinalIgnoreCase);
        var stored = await env.Repository.GetAccountAsync(accountId);
        Assert.NotNull(stored);
        Assert.Equal(AccountLifecycle.Pending, stored!.Lifecycle);
        Assert.True(Directory.Exists(stored.Profile.CodexHome));
    }

    [Fact]
    public async Task Failed_first_onboarding_rolls_back_in_background_even_when_ui_never_polls_status()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await using var server = env.CreateServer();
        await server.StartAsync();
        var client = new RouterControlClient(env.Root);

        var start = await client.StartOnboardAsync("Abandoned Account");
        var accountId = new AccountId(start.AccountId);
        Assert.Empty(await env.Repository.ListAccountsAsync());
        Assert.Equal(AccountLifecycle.Pending, (await env.Repository.GetAccountAsync(accountId))!.Lifecycle);

        env.OfficialBrowserLoginFactory.CompleteNext(success: false, error: "official login abandoned");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (await env.Repository.GetAccountAsync(accountId) is not null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Null(await env.Repository.GetAccountAsync(accountId));
        Assert.Empty(await env.Repository.ListAllAccountsAsync());
        Assert.False(Directory.Exists(Path.Combine(env.Root, "profiles", accountId.Value)));
    }

    [Fact]
    public async Task Server_start_removes_pending_onboarding_left_by_a_previous_crash()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var accountId = new AccountId("stale-pending");
        var template = await env.Materializer.ImportSharedTemplateAsync(env.SourceCodexHome);
        var materialized = await env.Materializer.MaterializeAsync(accountId, template);
        var profile = new AccountProfile(accountId, "Stale Pending", materialized.CodexHome, Enabled: false);
        await env.Repository.CreateAccountAsync(profile, lifecycle: AccountLifecycle.Pending);
        Assert.Single(await env.Repository.ListAllAccountsAsync());
        Assert.Empty(await env.Repository.ListAccountsAsync());

        var credentialWriter = new FakeCredentialWriter();
        await using var server = env.CreateServer(credentialWriter: credentialWriter);
        await server.StartAsync();

        Assert.True(credentialWriter.DeleteCalled);
        Assert.Equal(materialized.CodexHome, credentialWriter.CodexHome);
        Assert.Null(await env.Repository.GetAccountAsync(accountId));
        Assert.Empty(await env.Repository.ListAllAccountsAsync());
        Assert.False(Directory.Exists(Path.Combine(env.Root, "profiles", accountId.Value)));
    }

    [Fact]
    public async Task Server_start_preserves_pending_profile_when_keyring_cleanup_fails()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var accountId = new AccountId("stale-pending-keyring-failure");
        var template = await env.Materializer.ImportSharedTemplateAsync(env.SourceCodexHome);
        var materialized = await env.Materializer.MaterializeAsync(accountId, template);
        var profile = new AccountProfile(accountId, "Stale Pending", materialized.CodexHome, Enabled: false);
        await env.Repository.CreateAccountAsync(profile, lifecycle: AccountLifecycle.Pending);
        var credentialWriter = new FakeCredentialWriter { DeleteFailure = new InvalidOperationException("credential store unavailable") };
        await using var server = env.CreateServer(credentialWriter: credentialWriter);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync());

        Assert.Contains("credential store unavailable", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await env.Repository.GetAccountAsync(accountId));
        Assert.True(Directory.Exists(Path.Combine(env.Root, "profiles", accountId.Value)));
    }

    [Fact]
    public async Task Failed_relogin_preserves_existing_account_profile()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var existing = await env.AddAccountAsync("existing");
        await using var server = env.CreateServer();
        await server.StartAsync();
        var client = new RouterControlClient(env.Root);

        var start = await client.StartLoginAsync(existing.Id.Value);
        env.Factory.Latest(existing.Id).EmitLoginCompleted(start.LoginId, success: false);

        var status = await WaitForLoginAsync(client, start.LoginId);

        Assert.Equal("failed", status.State);
        Assert.NotNull(await env.Repository.GetAccountAsync(existing.Id));
        Assert.True(Directory.Exists(existing.CodexHome));
    }

    private static string CreateWebSessionJson(string marker)
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}"));
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["sub"] = "user-imported",
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            ["email"] = "imported@example.test",
            ["marker"] = marker,
            ["https://api.openai.com/auth"] = new
            {
                chatgpt_user_id = "user-imported",
                chatgpt_account_id = "account-imported",
                chatgpt_plan_type = "plus",
                chatgpt_account_is_fedramp = false
            }
        });
        var token = $"{header}.{Encode(payload)}.signature-{marker}";
        return JsonSerializer.Serialize(new
        {
            accessToken = token,
            account = new { id = "account-imported", planType = "plus" },
            user = new { email = "imported@example.test" }
        });
    }

    private static async Task<ControlLoginStatus> WaitForLoginAsync(RouterControlClient client, string loginId)
    {
        ControlLoginStatus status;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            status = await client.LoginStatusAsync(loginId);
            if (status.State == "pending") await Task.Delay(20);
        } while (status.State == "pending" && DateTime.UtcNow < deadline);
        return status;
    }

    private sealed class TestEnvironment : IAsyncDisposable
    {
        private TestEnvironment(
            string root,
            StorageDatabase database,
            RouterRepository repository,
            ProfileMaterializer materializer,
            FakeWorkerFactory factory,
            WorkerPool pool,
            string sourceCodexHome,
            FakeCodexCliLoginRunner cliLoginRunner,
            FakeCodexDesktopLoginRunner desktopLoginRunner,
            FakeOfficialBrowserLoginFactory officialBrowserLoginFactory)
        {
            Root = root;
            Database = database;
            Repository = repository;
            Materializer = materializer;
            Factory = factory;
            Pool = pool;
            SourceCodexHome = sourceCodexHome;
            CliLoginRunner = cliLoginRunner;
            DesktopLoginRunner = desktopLoginRunner;
            OfficialBrowserLoginFactory = officialBrowserLoginFactory;
        }

        public string Root { get; }
        public StorageDatabase Database { get; }
        public RouterRepository Repository { get; }
        public ProfileMaterializer Materializer { get; }
        public FakeWorkerFactory Factory { get; }
        public WorkerPool Pool { get; }
        public string SourceCodexHome { get; }
        public FakeCodexCliLoginRunner CliLoginRunner { get; }
        public FakeCodexDesktopLoginRunner DesktopLoginRunner { get; }
        public FakeOfficialBrowserLoginFactory OfficialBrowserLoginFactory { get; }

        public static async Task<TestEnvironment> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"codex-router-control-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var database = new StorageDatabase(new StorageOptions(Path.Combine(root, "router.db")));
            await database.InitializeAsync();
            var repository = new RouterRepository(database);
            var materializer = new ProfileMaterializer(new ProfileLayout(root));
            var source = Path.Combine(root, "source-codex");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "config.toml"), "model = \"gpt-5.6-codex\"\n");
            var factory = new FakeWorkerFactory();
            var pool = new WorkerPool(factory, new WorkerPoolOptions(
                MaxResidentWorkers: 5,
                IdleTtl: TimeSpan.FromHours(1),
                MaintenanceInterval: TimeSpan.FromHours(1)));
            var cliLoginRunner = new FakeCodexCliLoginRunner();
            var desktopLoginRunner = new FakeCodexDesktopLoginRunner();
            var officialBrowserLoginFactory = new FakeOfficialBrowserLoginFactory();
            return new TestEnvironment(root, database, repository, materializer, factory, pool, source, cliLoginRunner, desktopLoginRunner, officialBrowserLoginFactory);
        }

        public RouterControlServer CreateServer(
            IAgentIdentityRegistrar? agentIdentityRegistrar = null,
            ICodexCredentialWriter? credentialWriter = null) =>
            new(
                Root,
                Repository,
                Pool,
                Materializer,
                SourceCodexHome,
                new CodexRouter.Accounts.AccountServiceOptions(LoginTimeout: TimeSpan.FromSeconds(5)),
                nativeCodexExecutable: Path.Combine(Root, "codex.exe"),
                cliLoginRunner: CliLoginRunner,
                desktopExecutable: Path.Combine(Root, "ChatGPT.exe"),
                desktopLoginRunner: DesktopLoginRunner,
                officialBrowserLoginFactory: OfficialBrowserLoginFactory,
                agentIdentityRegistrar: agentIdentityRegistrar,
                credentialWriter: credentialWriter);

        public async Task<AccountProfile> AddAccountAsync(string id)
        {
            var accountId = new AccountId(id);
            var home = Path.Combine(Root, "profiles", id, "codex-home");
            Directory.CreateDirectory(home);
            var profile = new AccountProfile(accountId, id.ToUpperInvariant(), home, $"{id}@example.test", "plus");
            await Repository.CreateAccountAsync(profile);
            await Repository.AppendQuotaSnapshotAsync(new QuotaSnapshot(accountId, new[]
            {
                new QuotaBucket("codex", "Codex", QuotaBucketSlot.Primary, 20, TimeSpan.FromHours(5), DateTimeOffset.UtcNow.AddHours(2))
            }, DateTimeOffset.UtcNow));
            await Repository.AppendHealthEventAsync(new AccountHealth(accountId, AccountHealthState.Healthy, DateTimeOffset.UtcNow));
            return profile;
        }

        public async ValueTask DisposeAsync()
        {
            await Pool.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class FakeAgentIdentityRegistrar : IAgentIdentityRegistrar
    {
        public ChatGptSessionImport? SeenSession { get; private set; }
        public string? SeenProxyUrl { get; private set; }
        public Exception? Failure { get; init; }

        public Task<CodexAgentIdentityRecord> RegisterAsync(
            ChatGptSessionImport session,
            string? proxyUrl,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SeenSession = session;
            SeenProxyUrl = proxyUrl;
            if (Failure is not null) return Task.FromException<CodexAgentIdentityRecord>(Failure);
            return Task.FromResult(new CodexAgentIdentityRecord(
                "runtime-imported",
                "synthetic-pkcs8",
                session.AccountId,
                session.ChatGptUserId,
                session.Email,
                session.PlanType,
                session.IsFedRamp));
        }
    }

    private sealed class FakeCredentialWriter : ICodexCredentialWriter
    {
        public bool SaveCalled { get; private set; }
        public bool DeleteCalled { get; private set; }
        public string? CodexHome { get; private set; }
        public CodexAgentIdentityRecord? SavedIdentity { get; private set; }
        public Exception? DeleteFailure { get; init; }

        public Task SaveAgentIdentityAsync(
            string codexHome,
            CodexAgentIdentityRecord identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCalled = true;
            CodexHome = codexHome;
            SavedIdentity = identity;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string codexHome, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCalled = true;
            CodexHome = codexHome;
            return DeleteFailure is null ? Task.CompletedTask : Task.FromException(DeleteFailure);
        }
    }

    private sealed class FakeOfficialBrowserLoginFactory : IOfficialAppServerLoginSessionFactory
    {
        private readonly object _gate = new();
        private readonly Queue<FakeSession> _pending = new();
        private readonly Queue<Invocation> _invocations = new();

        public Task<IOfficialAppServerLoginSession> StartAsync(
            string codexExecutable,
            AccountProfile profile,
            string? proxyUrl,
            bool deviceCode = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authUrl = deviceCode
                ? new Uri("https://chatgpt.com/device")
                : new Uri($"https://auth.openai.com/oauth/authorize?state={profile.Id.Value}&code_challenge=test");
            var session = new FakeSession(
                profile.Id,
                $"official-{profile.Id.Value}",
                authUrl,
                deviceCode ? "ABCD-EFGH" : null);
            lock (_gate)
            {
                _pending.Enqueue(session);
                _invocations.Enqueue(new Invocation(codexExecutable, profile.CodexHome, proxyUrl, authUrl, deviceCode));
            }
            return Task.FromResult<IOfficialAppServerLoginSession>(session);
        }

        public async Task<Invocation> WaitForInvocationAsync(int timeoutMs = 3000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    if (_invocations.Count > 0) return _invocations.Peek();
                }
                await Task.Delay(20);
            }
            lock (_gate)
            {
                Assert.NotEmpty(_invocations);
                return _invocations.Peek();
            }
        }

        public void CompleteNext(bool success, string? error = null)
        {
            FakeSession session;
            lock (_gate)
            {
                Assert.NotEmpty(_pending);
                session = _pending.Dequeue();
                if (_invocations.Count > 0) _invocations.Dequeue();
            }
            session.Complete(success, error);
        }

        public sealed record Invocation(string CodexExecutable, string CodexHome, string? ProxyUrl, Uri AuthUrl, bool DeviceCode);

        private sealed class FakeSession : IOfficialAppServerLoginSession
        {
            private readonly TaskCompletionSource<OfficialAppServerLoginCompletion> _completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public FakeSession(AccountId accountId, string loginId, Uri authUrl, string? userCode)
            {
                AccountId = accountId;
                LoginId = loginId;
                AuthUrl = authUrl;
                UserCode = userCode;
                StartedAt = DateTimeOffset.UtcNow;
            }

            public AccountId AccountId { get; }
            public string LoginId { get; }
            public Uri AuthUrl { get; }
            public string? UserCode { get; }
            public DateTimeOffset StartedAt { get; }

            public Task<OfficialAppServerLoginCompletion> WaitForCompletionAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
                _completion.Task.WaitAsync(timeout, cancellationToken);

            public Task CancelAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _completion.TrySetResult(new OfficialAppServerLoginCompletion(false, "canceled", DateTimeOffset.UtcNow));
                return Task.CompletedTask;
            }

            public void Complete(bool success, string? error) =>
                _completion.TrySetResult(new OfficialAppServerLoginCompletion(success, error, DateTimeOffset.UtcNow));

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCodexCliLoginRunner : ICodexCliLoginRunner
    {
        private readonly object _gate = new();
        private readonly Queue<TaskCompletionSource<CodexCliLoginResult>> _pending = new();
        private readonly List<Invocation> _invocations = new();

        public Task<CodexCliLoginResult> RunAsync(
            string codexExecutable,
            string codexHome,
            bool deviceAuth = false,
            string? proxyUrl = null,
            CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<CodexCliLoginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            }
            lock (_gate)
            {
                _invocations.Add(new Invocation(deviceAuth, proxyUrl));
                _pending.Enqueue(completion);
            }
            return completion.Task;
        }

        public async Task<Invocation> WaitForInvocationAsync(bool deviceAuth, int timeoutMs = 3000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    var invocation = _invocations.LastOrDefault(item => item.DeviceAuth == deviceAuth);
                    if (invocation is not null) return invocation;
                }
                await Task.Delay(20);
            }
            lock (_gate)
            {
                var invocation = _invocations.LastOrDefault(item => item.DeviceAuth == deviceAuth);
                Assert.NotNull(invocation);
                return invocation!;
            }
        }

        public sealed record Invocation(bool DeviceAuth, string? ProxyUrl);

        public void CompleteNext(bool success, string? error = null)
        {
            TaskCompletionSource<CodexCliLoginResult> completion;
            lock (_gate)
            {
                Assert.NotEmpty(_pending);
                completion = _pending.Dequeue();
            }
            completion.TrySetResult(new CodexCliLoginResult(success, success ? 0 : 1, error));
        }
    }

    private sealed class FakeCodexDesktopLoginRunner : ICodexDesktopLoginRunner
    {
        private readonly object _gate = new();
        private readonly Queue<TaskCompletionSource<CodexDesktopLoginResult>> _pending = new();
        private readonly Queue<Invocation> _invocations = new();

        public Task<CodexDesktopLoginResult> RunAsync(
            string desktopExecutable,
            string codexExecutable,
            string codexHome,
            TimeSpan timeout,
            string? proxyUrl = null,
            CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<CodexDesktopLoginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            }
            lock (_gate)
            {
                _invocations.Enqueue(new Invocation(desktopExecutable, codexExecutable, codexHome, timeout, proxyUrl));
                _pending.Enqueue(completion);
            }
            return completion.Task;
        }

        public async Task<Invocation> WaitForInvocationAsync(int timeoutMs = 3000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    if (_invocations.Count > 0) return _invocations.Peek();
                }
                await Task.Delay(20);
            }
            lock (_gate)
            {
                Assert.NotEmpty(_invocations);
                return _invocations.Peek();
            }
        }

        public void CompleteNext(bool success, string? error = null)
        {
            TaskCompletionSource<CodexDesktopLoginResult> completion;
            lock (_gate)
            {
                Assert.NotEmpty(_pending);
                completion = _pending.Dequeue();
                if (_invocations.Count > 0) _invocations.Dequeue();
            }
            completion.TrySetResult(new CodexDesktopLoginResult(success, error));
        }

        public sealed record Invocation(string DesktopExecutable, string CodexExecutable, string CodexHome, TimeSpan Timeout, string? ProxyUrl);
    }

    private sealed class FakeWorkerFactory : IAppServerWorkerFactory
    {
        private readonly Dictionary<string, FakeWorker> _latest = new(StringComparer.Ordinal);
        private int _sequence;
        public FakeWorker Latest(AccountId accountId) => _latest[accountId.Value];
        public IAppServerWorker Create(AccountProfile profile)
        {
            var worker = new FakeWorker(new WorkerId($"{profile.Id.Value}-{++_sequence}"), profile.Id);
            _latest[profile.Id.Value] = worker;
            return worker;
        }
    }

    private sealed class FakeWorker : IAppServerWorker
    {
        public FakeWorker(WorkerId workerId, AccountId accountId)
        {
            WorkerId = workerId;
            AccountId = accountId;
        }

        public WorkerId WorkerId { get; }
        public AccountId AccountId { get; }
        public WorkerState State { get; private set; } = WorkerState.Stopped;
        public int? ProcessId => IsAlive ? 4321 : null;
        public bool IsAlive => State is WorkerState.Ready or WorkerState.Busy or WorkerState.Draining;
        public string SeededText { get; private set; } = string.Empty;
        private int _migrationThreadSequence;
        public event EventHandler<WorkerStateChange>? StateChanged;
        public event EventHandler<WorkerNotification>? NotificationReceived;
        public event EventHandler<WorkerServerRequest>? ServerRequestReceived { add { } remove { } }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Change(WorkerState.Ready);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Change(WorkerState.Stopped);
            return Task.CompletedTask;
        }

        public Task<JsonElement> SendRequestAsync(string method, object? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            if (method == "thread/read")
            {
                return Task.FromResult(Parse("""
                    {"thread":{"id":"source-thread","cwd":"C:/repo","status":{"type":"idle"},"turns":[{"status":"completed","items":[
                      {"type":"userMessage","text":"continue the visible task"},
                      {"type":"agentMessage","text":"visible work result"}
                    ]}]}}
                    """));
            }
            if (method == "thread/start")
            {
                var id = $"migrated-{AccountId.Value}-{Interlocked.Increment(ref _migrationThreadSequence)}";
                return Task.FromResult(Parse($"{{\"thread\":{{\"id\":\"{id}\"}}}}"));
            }
            if (method == "turn/start")
            {
                using var document = JsonDocument.Parse(JsonSerializer.Serialize(parameters));
                SeededText = document.RootElement.GetProperty("input")[0].GetProperty("text").GetString() ?? string.Empty;
                return Task.FromResult(Parse("{\"turn\":{\"id\":\"migration-turn\",\"status\":\"completed\",\"items\":[]}}"));
            }
            return method switch
            {
                "account/login/start" => Task.FromResult(Parse($"{{\"type\":\"chatgpt\",\"loginId\":\"login-{AccountId.Value}\",\"authUrl\":\"https://auth.example.test/{AccountId.Value}\"}}")),
                "account/login/cancel" => Task.FromResult(Parse("{\"status\":\"canceled\"}")),
                "account/read" => Task.FromResult(Parse("{\"account\":{\"type\":\"chatgpt\",\"email\":\"fresh@example.test\",\"planType\":\"plus\"},\"requiresOpenaiAuth\":true}")),
                "account/rateLimits/read" => Task.FromResult(Parse("{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":25,\"windowDurationMins\":300},\"secondary\":null,\"planType\":\"plus\"}}")),
                "account/logout" => Task.FromResult(Parse("{}")),
                _ => Task.FromException<JsonElement>(new AppServerRpcException(-32601, $"unsupported {method}"))
            };
        }

        public Task<JsonElement> SendRetryableRequestAsync(string method, object? parameters, DateTimeOffset deadline, bool retryable, RetryPolicy? policy = null, CancellationToken cancellationToken = default) =>
            SendRequestAsync(method, parameters, deadline - DateTimeOffset.UtcNow, cancellationToken);
        public Task SendNotificationAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RespondToServerRequestAsync(WorkerServerRequest request, object? result = null, RpcErrorPayload? error = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async IAsyncEnumerable<WorkerNotification> ReadNotificationsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<WorkerServerRequest> ReadServerRequestsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
        public IReadOnlyList<string> GetRecentStderr() => Array.Empty<string>();

        public void EmitLoginCompleted(string loginId, bool success)
        {
            NotificationReceived?.Invoke(this, new WorkerNotification(
                WorkerId,
                AccountId,
                "account/login/completed",
                Parse($"{{\"loginId\":\"{loginId}\",\"success\":{success.ToString().ToLowerInvariant()},\"error\":null}}"),
                DateTimeOffset.UtcNow));
        }

        public ValueTask DisposeAsync()
        {
            Change(WorkerState.Stopped);
            return ValueTask.CompletedTask;
        }

        private void Change(WorkerState next)
        {
            var previous = State;
            State = next;
            StateChanged?.Invoke(this, new WorkerStateChange(WorkerId, AccountId, previous, next, null, DateTimeOffset.UtcNow));
        }

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }
}
