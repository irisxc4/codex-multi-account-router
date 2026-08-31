using CodexRouter.Accounts;
using CodexRouter.Domain;
using CodexRouter.Storage;
using Xunit;

namespace CodexRouter.Accounts.Tests;

public sealed class AccountServiceTests
{
    [Fact]
    public async Task Three_profiles_coexist_with_isolated_codex_homes()
    {
        await using var env = await AccountTestEnvironment.CreateAsync();

        var a = await env.CreateAccountAsync("a");
        var b = await env.CreateAccountAsync("b");
        var c = await env.CreateAccountAsync("c");
        var accounts = await env.Service.ListAccountsAsync();

        Assert.Equal(3, accounts.Count);
        Assert.Equal(3, new[] { a.CodexHome, b.CodexHome, c.CodexHome }.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(accounts, account =>
        {
            var config = File.ReadAllText(Path.Combine(account.Profile.CodexHome, "config.toml"));
            Assert.Contains("keyring", config);
        });
    }

    [Fact]
    public async Task ChatGpt_login_flow_opens_browser_and_refreshes_metadata_without_router_tokens()
    {
        await using var env = await AccountTestEnvironment.CreateAsync();
        var profile = await env.CreateAccountAsync("a");
        env.Factory.Configure(profile.Id).AccountRead = AccountTestEnvironment.FakeWorkerConfiguration.Parse("""
            {"account":{"type":"chatgpt","email":"a@example.com","planType":"pro"},"requiresOpenaiAuth":true}
            """);

        await using var login = await env.Service.BeginChatGptLoginAsync(profile.Id, openBrowser: true);
        Assert.Single(env.Launcher.Opened);
        Assert.Equal("login-a", login.LoginId);

        env.Factory.Latest(profile.Id).Emit("account/login/completed", """
            {"loginId":"login-a","success":true,"error":null}
            """);
        var updated = await env.Service.CompleteChatGptLoginAsync(login);

        Assert.Equal("a@example.com", updated.Email);
        Assert.Equal("pro", updated.PlanType);
        Assert.False(File.Exists(Path.Combine(updated.CodexHome, "auth.json")));
        var schemaText = await ReadDatabaseSchemaAsync(env.Database);
        Assert.DoesNotContain("access_token", schemaText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh_token", schemaText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Onboarding_account_stays_pending_and_hidden_until_oauth_and_account_read_succeed()
    {
        await using var env = await AccountTestEnvironment.CreateAsync();
        var onboard = await env.Service.OnboardChatGptAsync("Pending", env.Template, openBrowser: false);
        var accountId = onboard.Profile.Id;

        var stored = await env.Repository.GetAccountAsync(accountId);
        Assert.NotNull(stored);
        Assert.Equal(AccountLifecycle.Pending, stored!.Lifecycle);
        Assert.False(stored.Profile.Enabled);
        Assert.Empty(await env.Service.ListAccountsAsync());

        env.Factory.Latest(accountId).Emit("account/login/completed", $"{{\"loginId\":\"{onboard.LoginSession.LoginId}\",\"success\":true,\"error\":null}}");
        var activated = await env.Service.CompleteChatGptLoginAsync(onboard.LoginSession);

        Assert.True(activated.Enabled);
        stored = await env.Repository.GetAccountAsync(accountId);
        Assert.NotNull(stored);
        Assert.Equal(AccountLifecycle.Active, stored!.Lifecycle);
        Assert.True(stored.Profile.Enabled);
        Assert.Single(await env.Service.ListAccountsAsync());
    }

    [Fact]
    public async Task Full_quota_supports_zero_one_two_and_new_limit_buckets()
    {
        await using var env = await AccountTestEnvironment.CreateAsync();
        var profile = await env.CreateAccountAsync("a");
        var config = env.Factory.Configure(profile.Id);

        config.RateLimitsRead = AccountTestEnvironment.FakeWorkerConfiguration.Parse("""
            {"rateLimits":{"limitId":"codex","primary":null,"secondary":null,"planType":"plus"}}
            """);
        Assert.Empty((await env.Service.RefreshQuotaAsync(profile.Id)).Buckets);

        config.RateLimitsRead = AccountTestEnvironment.FakeWorkerConfiguration.Parse("""
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":10,"windowDurationMins":300,"resetsAt":1786845600},"secondary":null,"planType":"plus"}}
            """);
        Assert.Single((await env.Service.RefreshQuotaAsync(profile.Id)).Buckets);

        config.RateLimitsRead = AccountTestEnvironment.FakeWorkerConfiguration.Parse("""
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":10,"windowDurationMins":300,"resetsAt":1786845600},"secondary":{"usedPercent":20,"windowDurationMins":10080,"resetsAt":1787443200},"planType":"plus"}}
            """);
        Assert.Equal(2, (await env.Service.RefreshQuotaAsync(profile.Id)).Buckets.Count);

        config.RateLimitsRead = AccountTestEnvironment.FakeWorkerConfiguration.Parse("""
            {"rateLimitsByLimitId":{"codex":{"limitId":"codex","primary":{"usedPercent":10,"windowDurationMins":300},"secondary":null,"planType":"plus"},"review":{"limitId":"review","primary":{"usedPercent":55,"windowDurationMins":1440},"secondary":null}}}
            """);
        var multi = await env.Service.RefreshQuotaAsync(profile.Id);
        Assert.Equal(2, multi.Buckets.Count);
        Assert.Contains(multi.Buckets, bucket => bucket.LimitId == "review");
    }

    [Fact]
    public async Task Failed_or_empty_full_read_keeps_last_known_good_quota()
    {
        await using var env = await AccountTestEnvironment.CreateAsync();
        var profile = await env.CreateAccountAsync("a");
        var config = env.Factory.Configure(profile.Id);

        var baseline = await env.Service.RefreshQuotaAsync(profile.Id);
        Assert.Equal(2, baseline.Buckets.Count);

        config.RateLimitsRead = AccountTestEnvironment.FakeWorkerConfiguration.Parse("{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":null,\"secondary\":null}}");
        var refreshed = await env.Service.RefreshQuotaAsync(profile.Id);

        Assert.Equal(baseline.Buckets, refreshed.Buckets);
        Assert.Equal(baseline.FetchedAt.ToUnixTimeMilliseconds(), refreshed.FetchedAt.ToUnixTimeMilliseconds());
        Assert.Single(await env.Repository.GetQuotaHistoryAsync(profile.Id));
    }

    [Fact]
    public async Task Concurrent_quota_refreshes_are_coalesced_per_account()
    {
        await using var env = await AccountTestEnvironment.CreateAsync();
        var profile = await env.CreateAccountAsync("a");
        var configuration = env.Factory.Configure(profile.Id);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        configuration.RateLimitsReadGate = gate;
        var callsBefore = configuration.Calls.Count(call => call.Method == "account/rateLimits/read");

        var refreshes = new[]
        {
            env.Service.RefreshQuotaAsync(profile.Id),
            env.Service.RefreshQuotaAsync(profile.Id),
            env.Service.RefreshQuotaAsync(profile.Id)
        };
        await WaitUntilAsync(() => configuration.Calls.Count(call => call.Method == "account/rateLimits/read") == callsBefore + 1);
        gate.SetResult(true);
        var results = await Task.WhenAll(refreshes);

        Assert.All(results, result => Assert.Equal(results[0].Buckets, result.Buckets));
        var callsAfter = configuration.Calls.Count(call => call.Method == "account/rateLimits/read");
        Assert.Equal(callsBefore + 1, callsAfter);
    }

    [Fact]
    public async Task Background_refresh_prefetches_new_active_accounts()
    {
        await using var env = await AccountTestEnvironment.CreateAsync(new AccountServiceOptions(
            LoginTimeout: TimeSpan.FromSeconds(5),
            QuotaStaleAfter: TimeSpan.FromMinutes(5),
            QuotaRefreshInterval: TimeSpan.FromMilliseconds(20)));
        var profile = await env.CreateAccountAsync("background");

        await WaitUntilAsync(async () =>
        {
            var snapshot = await env.Repository.GetLatestQuotaSnapshotAsync(profile.Id);
            return snapshot is { Buckets.Count: 2 };
        });

        Assert.Contains(env.Factory.Configure(profile.Id).Calls, call => call.Method == "account/rateLimits/read");
    }

    [Fact]
    public async Task Sparse_notification_merges_into_latest_snapshot_and_preserves_nullable_metadata()
    {
        await using var env = await AccountTestEnvironment.CreateAsync();
        var profile = await env.CreateAccountAsync("a");
        var baseline = await env.Service.RefreshQuotaAsync(profile.Id);
        Assert.Equal("plus", baseline.PlanType);

        var worker = env.Factory.Latest(profile.Id);
        worker.Emit("account/rateLimits/updated", """
            {"rateLimits":{"limitId":"codex","planType":null,"primary":{"usedPercent":61,"windowDurationMins":null,"resetsAt":null},"spendControlReached":true}}
            """);

        await WaitUntilAsync(async () => (await env.Repository.GetQuotaHistoryAsync(profile.Id)).Count >= 2);
        var merged = await env.Repository.GetLatestQuotaSnapshotAsync(profile.Id);

        Assert.NotNull(merged);
        Assert.Equal("plus", merged!.PlanType);
        Assert.True(merged.SpendControlReached);
        var primary = merged.Buckets.Single(bucket => bucket.Slot == QuotaBucketSlot.Primary);
        Assert.Equal(61, primary.UsedPercent);
        Assert.Equal(TimeSpan.FromMinutes(300), primary.WindowDuration);
    }

    [Fact]
    public async Task Authentication_required_state_recovers_after_login_and_quota_refresh()
    {
        await using var env = await AccountTestEnvironment.CreateAsync();
        var profile = await env.CreateAccountAsync("a");
        var config = env.Factory.Configure(profile.Id);
        config.AccountRead = AccountTestEnvironment.FakeWorkerConfiguration.Parse("""
            {"account":null,"requiresOpenaiAuth":true}
            """);

        _ = await env.Service.RefreshAccountAsync(profile.Id);
        var events = await env.Repository.GetHealthEventsAsync(profile.Id);
        Assert.Equal(AccountHealthState.AuthRequired, events[0].Health.State);

        config.AccountRead = AccountTestEnvironment.FakeWorkerConfiguration.Parse("""
            {"account":{"type":"chatgpt","email":"a@example.com","planType":"plus"},"requiresOpenaiAuth":true}
            """);
        _ = await env.Service.RefreshQuotaAsync(profile.Id);
        _ = await env.Service.RefreshAccountAsync(profile.Id);
        events = await env.Repository.GetHealthEventsAsync(profile.Id);
        Assert.Equal(AccountHealthState.Healthy, events[0].Health.State);
    }

    [Fact]
    public async Task Rate_limit_enters_cooldown_with_reset_time()
    {
        await using var env = await AccountTestEnvironment.CreateAsync();
        var profile = await env.CreateAccountAsync("a");
        var reset = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        env.Factory.Configure(profile.Id).RateLimitsRead = AccountTestEnvironment.FakeWorkerConfiguration.Parse(
            "{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":100,\"windowDurationMins\":300,\"resetsAt\":" +
            reset +
            "},\"secondary\":null,\"planType\":\"plus\",\"rateLimitReachedType\":\"rate_limit_reached\"}}");

        _ = await env.Service.RefreshQuotaAsync(profile.Id);
        var health = (await env.Repository.GetHealthEventsAsync(profile.Id))[0].Health;

        Assert.Equal(AccountHealthState.Cooldown, health.State);
        Assert.NotNull(health.CooldownUntil);
    }

    [Fact]
    public void Unrelated_model_limit_does_not_drain_account_health()
    {
        var accountId = new AccountId("a");
        var profile = new AccountProfile(accountId, "A", Path.Combine(Path.GetTempPath(), "health-a"));
        var quota = new QuotaSnapshot(accountId, new[]
        {
            new QuotaBucket("codex", "Codex", QuotaBucketSlot.Primary, 20,
                TimeSpan.FromHours(5), DateTimeOffset.UtcNow.AddHours(2)),
            new QuotaBucket("codex_bengalfox", "GPT-5.3-Codex-Spark", QuotaBucketSlot.Primary, 100,
                TimeSpan.FromHours(5), DateTimeOffset.UtcNow.AddHours(2))
        }, DateTimeOffset.UtcNow);

        var health = new AccountHealthEvaluator().Evaluate(profile, null, quota, DateTimeOffset.UtcNow);

        Assert.Equal(AccountHealthState.Healthy, health.State);
    }

    [Fact]
    public async Task Usage_unsupported_is_a_capability_fallback_not_a_service_failure()
    {
        await using var env = await AccountTestEnvironment.CreateAsync();
        var profile = await env.CreateAccountAsync("a");
        env.Factory.Configure(profile.Id).UsageUnsupported = true;

        var result = await env.Service.RefreshUsageAsync(profile.Id);

        Assert.Equal(UsageAvailability.Unsupported, result.Availability);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task Rename_and_enable_disable_update_profile_and_health()
    {
        await using var env = await AccountTestEnvironment.CreateAsync();
        var profile = await env.CreateAccountAsync("a");

        var renamed = await env.Service.RenameAsync(profile.Id, "Primary Account");
        Assert.Equal("Primary Account", renamed.Alias);
        var disabled = await env.Service.SetEnabledAsync(profile.Id, false);
        Assert.False(disabled.Enabled);
        Assert.Equal(AccountHealthState.Disabled, (await env.Repository.GetHealthEventsAsync(profile.Id))[0].Health.State);
        var enabled = await env.Service.SetEnabledAsync(profile.Id, true);
        Assert.True(enabled.Enabled);
    }

    [Fact]
    public async Task Delete_is_blocked_while_sticky_routes_exist()
    {
        await using var env = await AccountTestEnvironment.CreateAsync();
        var profile = await env.CreateAccountAsync("a");
        await env.Repository.InsertThreadRouteAsync(new ThreadRoute(
            new ThreadId("thread-a"), profile.Id, new WorkerId("worker-a"), RouteReason.AutoQuota,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<AccountDeleteBlockedException>(() => env.Service.DeleteAccountAsync(profile.Id));
        Assert.NotNull(await env.Repository.GetAccountAsync(profile.Id));
        Assert.True(Directory.Exists(Path.GetDirectoryName(profile.CodexHome)!));
    }

    [Fact]
    public async Task Pending_onboarding_cleanup_does_not_start_worker_or_logout_before_profile_deletion()
    {
        await using var env = await AccountTestEnvironment.CreateAsync();
        var profile = await env.Service.CreateAccountProfileAsync(
            "Pending",
            env.Template,
            new AccountId("pending-cleanup"),
            enabled: false,
            lifecycle: AccountLifecycle.Pending);
        var configuration = env.Factory.Configure(profile.Id);

        var removed = await env.Service.CleanupPendingOnboardingAsync();

        Assert.Equal(1, removed);
        Assert.Null(await env.Repository.GetAccountAsync(profile.Id));
        Assert.False(Directory.Exists(Path.GetDirectoryName(profile.CodexHome)!));
        Assert.DoesNotContain(configuration.Calls, call => call.Method == "account/logout");
    }

    [Fact]
    public async Task Safe_delete_logs_out_evicts_worker_removes_storage_and_profile()
    {
        await using var env = await AccountTestEnvironment.CreateAsync();
        var profile = await env.CreateAccountAsync("a");
        _ = await env.Service.RefreshAccountAsync(profile.Id);
        Assert.True(Directory.Exists(profile.CodexHome));

        await env.Service.DeleteAccountAsync(profile.Id);

        Assert.Null(await env.Repository.GetAccountAsync(profile.Id));
        Assert.False(Directory.Exists(Path.GetDirectoryName(profile.CodexHome)!));
        Assert.Contains(env.Factory.Configure(profile.Id).Calls, call => call.Method == "account/logout");
    }

    [Fact]
    public async Task Failed_logout_refuses_delete_unless_force_is_explicit()
    {
        await using var env = await AccountTestEnvironment.CreateAsync();
        var profile = await env.CreateAccountAsync("a");
        env.Factory.Configure(profile.Id).LogoutFails = true;

        await Assert.ThrowsAsync<AccountServiceException>(() => env.Service.DeleteAccountAsync(profile.Id));
        Assert.NotNull(await env.Repository.GetAccountAsync(profile.Id));
        Assert.True(Directory.Exists(profile.CodexHome));

        await env.Service.DeleteAccountAsync(profile.Id, force: true);
        Assert.Null(await env.Repository.GetAccountAsync(profile.Id));
    }

    private static async Task<string> ReadDatabaseSchemaAsync(StorageDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(sql, '\n') FROM sqlite_master WHERE sql IS NOT NULL;";
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
            {
                return;
            }
            await Task.Delay(20);
        }
        Assert.True(await predicate(), "Condition was not satisfied before timeout.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(20);
        }
        Assert.True(predicate(), "Condition was not satisfied before timeout.");
    }
}
