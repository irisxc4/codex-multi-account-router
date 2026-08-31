using System.Text.Json;
using CodexRouter.Domain;
using CodexRouter.Routing;
using CodexRouter.Rpc;
using CodexRouter.Storage;
using CodexRouter.Workers;
using Xunit;

namespace CodexRouter.Rpc.Tests;

public sealed class RpcMultiplexerTests
{
    [Fact]
    public async Task Handshake_then_thread_start_selects_best_account_and_persists_sticky_route()
    {
        await using var env = await RpcTestEnvironment.CreateAsync();
        await env.AddAccountAsync("a", 10);
        await env.AddAccountAsync("b", 70);
        await using var multiplexer = env.CreateMultiplexer();
        var reader = new RpcTestEnvironment.ChannelLineReader();
        var writer = new RpcTestEnvironment.ChannelLineWriter();
        var run = multiplexer.RunAsync(reader, writer);

        await InitializeAsync(reader, writer);
        reader.Send("{\"id\":2,\"method\":\"thread/start\",\"params\":{\"cwd\":\"C:/work\"}}");
        using var response = await ReadResponseByIdAsync(writer, "2");

        Assert.True(response.RootElement.TryGetProperty("result", out var result));
        var threadId = result.GetProperty("thread").GetProperty("id").GetString();
        Assert.NotNull(threadId);
        Assert.StartsWith("thread-a-", threadId, StringComparison.Ordinal);
        var route = await env.Repository.GetThreadRouteAsync(new ThreadId(threadId!));
        Assert.NotNull(route);
        Assert.Equal(new AccountId("a"), route!.AccountId);
        Assert.DoesNotContain("thread/start", env.Factory.Configure(new AccountId("a")).RetryableCalls);
        Assert.DoesNotContain("thread/start", env.Factory.Configure(new AccountId("b")).Calls);

        reader.Complete();
        await run;
    }

    [Fact]
    public async Task Thread_start_refreshes_stale_quota_before_selecting_account()
    {
        await using var env = await RpcTestEnvironment.CreateAsync();
        var profile = await env.AddAccountAsync("a", 80, DateTimeOffset.UtcNow.AddMinutes(-10));
        Assert.True(DateTimeOffset.UtcNow - (await env.Repository.GetLatestQuotaSnapshotAsync(profile.Id))!.FetchedAt > TimeSpan.FromMinutes(5));
        await env.Repository.AppendHealthEventAsync(new AccountHealth(
            profile.Id,
            AccountHealthState.Draining,
            DateTimeOffset.UtcNow,
            "previous quota reserve"));
        env.Factory.Configure(profile.Id).RateLimitsRead = RpcTestEnvironment.FakeWorkerConfiguration.Parse(
            "{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":5,\"windowDurationMins\":300},\"secondary\":null,\"planType\":\"plus\"}}");

        await using var multiplexer = env.CreateMultiplexer();
        var reader = new RpcTestEnvironment.ChannelLineReader();
        var writer = new RpcTestEnvironment.ChannelLineWriter();
        var run = multiplexer.RunAsync(reader, writer);
        await InitializeAsync(reader, writer);

        reader.Send("{\"id\":2,\"method\":\"thread/start\",\"params\":{\"cwd\":\"C:/work\"}}");
        using var response = await ReadResponseByIdAsync(writer, "2");

        Assert.True(response.RootElement.TryGetProperty("result", out _));
        Assert.Contains("account/rateLimits/read", env.Factory.Configure(profile.Id).Calls);
        var latest = await env.Repository.GetLatestQuotaSnapshotAsync(profile.Id);
        Assert.NotNull(latest);
        Assert.Equal(5, latest!.Buckets.Single().UsedPercent);
        Assert.Equal(AccountHealthState.Healthy, (await env.Repository.GetHealthEventsAsync(profile.Id))[0].Health.State);

        reader.Complete();
        await run;
    }

    [Fact]
    public async Task Model_context_matches_only_that_limit_and_keeps_general_cap()
    {
        await using var env = await RpcTestEnvironment.CreateAsync();
        var a = await env.AddAccountAsync("a", 20);
        var b = await env.AddAccountAsync("b", 60);
        await env.Repository.AppendQuotaSnapshotAsync(new QuotaSnapshot(
            a.Id,
            new[]
            {
                new QuotaBucket("codex", "Codex", QuotaBucketSlot.Primary, 20,
                    TimeSpan.FromHours(5), DateTimeOffset.UtcNow.AddHours(2)),
                new QuotaBucket("review", "Review", QuotaBucketSlot.Primary, 100,
                    TimeSpan.FromHours(5), DateTimeOffset.UtcNow.AddHours(2))
            },
            DateTimeOffset.UtcNow));
        await env.Repository.AppendQuotaSnapshotAsync(new QuotaSnapshot(
            b.Id,
            new[]
            {
                new QuotaBucket("codex", "Codex", QuotaBucketSlot.Primary, 60,
                    TimeSpan.FromHours(5), DateTimeOffset.UtcNow.AddHours(2)),
                new QuotaBucket("codex_bengalfox", "GPT-5.3-Codex-Spark", QuotaBucketSlot.Primary, 0,
                    TimeSpan.FromHours(5), DateTimeOffset.UtcNow.AddHours(2))
            },
            DateTimeOffset.UtcNow));

        await using var multiplexer = env.CreateMultiplexer();
        var reader = new RpcTestEnvironment.ChannelLineReader();
        var writer = new RpcTestEnvironment.ChannelLineWriter();
        var run = multiplexer.RunAsync(reader, writer);
        await InitializeAsync(reader, writer);

        reader.Send("{\"id\":2,\"method\":\"thread/start\",\"params\":{\"cwd\":\"C:/work\",\"config\":{\"model\":\"gpt-5.3-codex-spark\"}}}");
        using var response = await ReadResponseByIdAsync(writer, "2");

        var threadId = response.RootElement.GetProperty("result").GetProperty("thread").GetProperty("id").GetString();
        Assert.StartsWith("thread-a-", threadId, StringComparison.Ordinal);

        reader.Complete();
        await run;
    }

    [Fact]
    public async Task Read_of_background_thread_does_not_replace_current_thread_and_delete_clears_it()
    {
        await using var env = await RpcTestEnvironment.CreateAsync();
        await env.AddAccountAsync("a", 10, new RpcTestEnvironment.FakeThread("background-thread", 1, 1));
        await using var multiplexer = env.CreateMultiplexer();
        var reader = new RpcTestEnvironment.ChannelLineReader();
        var writer = new RpcTestEnvironment.ChannelLineWriter();
        var run = multiplexer.RunAsync(reader, writer);
        await InitializeAsync(reader, writer);

        reader.Send("{\"id\":2,\"method\":\"thread/start\",\"params\":{\"cwd\":\"C:/work\"}}");
        using var started = await ReadResponseByIdAsync(writer, "2");
        var currentThreadId = started.RootElement.GetProperty("result").GetProperty("thread").GetProperty("id").GetString()!;
        Assert.Equal(currentThreadId, (await env.Repository.GetRuntimeStateAsync("front_thread_id"))!.Value);

        reader.Send("{\"id\":3,\"method\":\"thread/read\",\"params\":{\"threadId\":\"background-thread\"}}");
        using var read = await ReadResponseByIdAsync(writer, "3");
        Assert.True(read.RootElement.TryGetProperty("result", out _));
        Assert.Equal(currentThreadId, (await env.Repository.GetRuntimeStateAsync("front_thread_id"))!.Value);

        reader.Send($"{{\"id\":4,\"method\":\"thread/delete\",\"params\":{{\"threadId\":{JsonSerializer.Serialize(currentThreadId)}}}}}");
        using var deleted = await ReadResponseByIdAsync(writer, "4");
        Assert.True(deleted.RootElement.TryGetProperty("result", out _));
        Assert.Null(await env.Repository.GetRuntimeStateAsync("front_thread_id"));

        reader.Complete();
        await run;
    }

    [Fact]
    public async Task Existing_thread_remains_sticky_after_router_restart_even_if_account_becomes_draining()
    {
        await using var env = await RpcTestEnvironment.CreateAsync();
        await env.AddAccountAsync("a", 10);
        await env.AddAccountAsync("b", 60);
        string threadId;

        await using (var first = env.CreateMultiplexer())
        {
            var reader = new RpcTestEnvironment.ChannelLineReader();
            var writer = new RpcTestEnvironment.ChannelLineWriter();
            var run = first.RunAsync(reader, writer);
            await InitializeAsync(reader, writer);
            reader.Send("{\"id\":2,\"method\":\"thread/start\",\"params\":{\"cwd\":\"C:/work\"}}");
            using var started = await ReadResponseByIdAsync(writer, "2");
            threadId = started.RootElement.GetProperty("result").GetProperty("thread").GetProperty("id").GetString()!;
            reader.Complete();
            await run;
        }

        await env.Repository.AppendHealthEventAsync(new AccountHealth(
            new AccountId("a"), AccountHealthState.Draining, DateTimeOffset.UtcNow, "quota low"));

        await using (var restarted = env.CreateMultiplexer())
        {
            var reader = new RpcTestEnvironment.ChannelLineReader();
            var writer = new RpcTestEnvironment.ChannelLineWriter();
            var run = restarted.RunAsync(reader, writer);
            await InitializeAsync(reader, writer);
            reader.Send($"{{\"id\":3,\"method\":\"turn/start\",\"params\":{{\"threadId\":\"{threadId}\",\"input\":[]}}}}");
            using var response = await ReadResponseByIdAsync(writer, "3");
            Assert.True(response.RootElement.TryGetProperty("result", out _));
            Assert.Contains("turn/start", env.Factory.Configure(new AccountId("a")).Calls);
            Assert.DoesNotContain("turn/start", env.Factory.Configure(new AccountId("b")).Calls);
            reader.Complete();
            await run;
        }
    }

    [Fact]
    public async Task Historical_thread_resume_discovers_owner_and_persists_recovery_route()
    {
        await using var env = await RpcTestEnvironment.CreateAsync();
        await env.AddAccountAsync("a", 20);
        await env.AddAccountAsync("b", 30, new RpcTestEnvironment.FakeThread("legacy-b", 10, 20));
        await using var multiplexer = env.CreateMultiplexer();
        var reader = new RpcTestEnvironment.ChannelLineReader();
        var writer = new RpcTestEnvironment.ChannelLineWriter();
        var run = multiplexer.RunAsync(reader, writer);

        await InitializeAsync(reader, writer);
        reader.Send("{\"id\":2,\"method\":\"thread/resume\",\"params\":{\"threadId\":\"legacy-b\"}}");
        using var response = await ReadResponseByIdAsync(writer, "2");

        Assert.True(response.RootElement.TryGetProperty("result", out _));
        var route = await env.Repository.GetThreadRouteAsync(new ThreadId("legacy-b"));
        Assert.NotNull(route);
        Assert.Equal(new AccountId("b"), route!.AccountId);
        Assert.Equal(RouteReason.Recovery, route.Reason);
        Assert.Contains("thread/resume", env.Factory.Configure(new AccountId("b")).Calls);
        Assert.DoesNotContain("thread/resume", env.Factory.Configure(new AccountId("a")).Calls);

        reader.Complete();
        await run;
    }

    [Fact]
    public async Task Duplicate_historical_thread_ownership_fails_closed()
    {
        await using var env = await RpcTestEnvironment.CreateAsync();
        var duplicate = new RpcTestEnvironment.FakeThread("dup", 1, 2);
        await env.AddAccountAsync("a", 20, duplicate);
        await env.AddAccountAsync("b", 30, duplicate);
        await using var multiplexer = env.CreateMultiplexer();
        var reader = new RpcTestEnvironment.ChannelLineReader();
        var writer = new RpcTestEnvironment.ChannelLineWriter();
        var run = multiplexer.RunAsync(reader, writer);

        await InitializeAsync(reader, writer);
        reader.Send("{\"id\":2,\"method\":\"thread/resume\",\"params\":{\"threadId\":\"dup\"}}");
        using var response = await ReadResponseByIdAsync(writer, "2");

        Assert.Equal(-32024, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Null(await env.Repository.GetThreadRouteAsync(new ThreadId("dup")));

        reader.Complete();
        await run;
    }

    [Fact]
    public async Task Thread_list_composite_cursor_has_no_duplicates_or_omissions_across_workers()
    {
        await using var env = await RpcTestEnvironment.CreateAsync();
        await env.AddAccountAsync("a", 20,
            new RpcTestEnvironment.FakeThread("a100", 100, 100),
            new RpcTestEnvironment.FakeThread("a080", 80, 80),
            new RpcTestEnvironment.FakeThread("a060", 60, 60));
        await env.AddAccountAsync("b", 30,
            new RpcTestEnvironment.FakeThread("b090", 90, 90),
            new RpcTestEnvironment.FakeThread("b070", 70, 70),
            new RpcTestEnvironment.FakeThread("b050", 50, 50));
        await using var multiplexer = env.CreateMultiplexer();
        var reader = new RpcTestEnvironment.ChannelLineReader();
        var writer = new RpcTestEnvironment.ChannelLineWriter();
        var run = multiplexer.RunAsync(reader, writer);
        await InitializeAsync(reader, writer);

        var all = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 3; page++)
        {
            var cursorJson = cursor is null ? "null" : JsonSerializer.Serialize(cursor);
            reader.Send($"{{\"id\":{page + 10},\"method\":\"thread/list\",\"params\":{{\"cursor\":{cursorJson},\"limit\":2,\"sortKey\":\"updated_at\"}}}}");
            using var response = await ReadResponseByIdAsync(writer, (page + 10).ToString());
            var result = response.RootElement.GetProperty("result");
            all.AddRange(result.GetProperty("data").EnumerateArray().Select(item => item.GetProperty("id").GetString()!));
            cursor = result.GetProperty("nextCursor").ValueKind == JsonValueKind.String
                ? result.GetProperty("nextCursor").GetString()
                : null;
        }

        Assert.Equal(new[] { "a100", "b090", "a080", "b070", "a060", "b050" }, all);
        Assert.Equal(6, all.Distinct(StringComparer.Ordinal).Count());
        Assert.Null(cursor);
        foreach (var id in all)
        {
            Assert.NotNull(await env.Repository.GetThreadRouteAsync(new ThreadId(id)));
        }

        reader.Complete();
        await run;
    }

    [Fact]
    public async Task Worker_server_requests_get_unique_front_ids_and_responses_return_to_original_worker_id()
    {
        await using var env = await RpcTestEnvironment.CreateAsync();
        await env.AddAccountAsync("a", 20);
        await env.AddAccountAsync("b", 30);
        await using var multiplexer = env.CreateMultiplexer();
        var reader = new RpcTestEnvironment.ChannelLineReader();
        var writer = new RpcTestEnvironment.ChannelLineWriter();
        var run = multiplexer.RunAsync(reader, writer);
        await InitializeAsync(reader, writer);

        multiplexer.Projection.Set(new AccountId("a"));
        reader.Send("{\"id\":2,\"method\":\"account/read\",\"params\":{\"refreshToken\":false}}");
        _ = await ReadResponseByIdAsync(writer, "2");
        multiplexer.Projection.Set(new AccountId("b"));
        reader.Send("{\"id\":3,\"method\":\"account/read\",\"params\":{\"refreshToken\":false}}");
        _ = await ReadResponseByIdAsync(writer, "3");

        env.Factory.Latest(new AccountId("a")).EmitServerRequest("native-1", "fake/approval", "{\"source\":\"a\"}");
        env.Factory.Latest(new AccountId("b")).EmitServerRequest("native-1", "fake/approval", "{\"source\":\"b\"}");
        using var requestA = await ReadMethodAsync(writer, "fake/approval");
        using var requestB = await ReadMethodAsync(writer, "fake/approval");
        var idA = requestA.RootElement.GetProperty("id").GetString()!;
        var idB = requestB.RootElement.GetProperty("id").GetString()!;
        Assert.NotEqual(idA, idB);

        reader.Send($"{{\"id\":{JsonSerializer.Serialize(idA)},\"result\":{{\"decision\":\"accept\"}}}}");
        reader.Send($"{{\"id\":{JsonSerializer.Serialize(idB)},\"result\":{{\"decision\":\"decline\"}}}}");
        await WaitUntilAsync(() =>
            env.Factory.Configure(new AccountId("a")).ServerResponses.Count == 1 &&
            env.Factory.Configure(new AccountId("b")).ServerResponses.Count == 1);

        Assert.Equal("native-1", env.Factory.Configure(new AccountId("a")).ServerResponses.Single().Request.Id.GetString());
        Assert.Equal("native-1", env.Factory.Configure(new AccountId("b")).ServerResponses.Single().Request.Id.GetString());

        reader.Complete();
        await run;
    }

    [Fact]
    public async Task Account_notifications_are_projected_not_aggregated()
    {
        await using var env = await RpcTestEnvironment.CreateAsync();
        await env.AddAccountAsync("a", 20);
        await env.AddAccountAsync("b", 30);
        await using var multiplexer = env.CreateMultiplexer();
        var reader = new RpcTestEnvironment.ChannelLineReader();
        var writer = new RpcTestEnvironment.ChannelLineWriter();
        var run = multiplexer.RunAsync(reader, writer);
        await InitializeAsync(reader, writer);

        multiplexer.Projection.Set(new AccountId("a"));
        reader.Send("{\"id\":2,\"method\":\"account/read\",\"params\":{}}");
        _ = await ReadResponseByIdAsync(writer, "2");
        multiplexer.Projection.Set(new AccountId("b"));
        reader.Send("{\"id\":3,\"method\":\"account/read\",\"params\":{}}");
        _ = await ReadResponseByIdAsync(writer, "3");

        env.Factory.Latest(new AccountId("a")).EmitNotification("account/updated", "{\"account\":null}");
        env.Factory.Latest(new AccountId("b")).EmitNotification("account/updated", "{\"account\":null}");
        using var notification = await ReadMethodAsync(writer, "account/updated");
        Assert.Equal("account/updated", notification.RootElement.GetProperty("method").GetString());

        await Task.Delay(150);
        var forwarded = writer.All.Count(line => line.Contains("\"method\":\"account/updated\"", StringComparison.Ordinal));
        Assert.Equal(1, forwarded);

        reader.Complete();
        await run;
    }

    [Fact]
    public async Task Router_off_is_single_account_pass_through_not_a_failure_mode()
    {
        await using var env = await RpcTestEnvironment.CreateAsync();
        await env.AddAccountAsync("a", 90,
            new RpcTestEnvironment.FakeThread("a-list", 1, 100));
        await env.AddAccountAsync("b", 5,
            new RpcTestEnvironment.FakeThread("b-list", 1, 200));
        var settings = await env.Repository.GetRouterSettingsAsync();
        await env.Repository.UpdateRouterSettingsAsync(settings with
        {
            Mode = RouterMode.Off,
            PinnedAccountId = null,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await using var multiplexer = env.CreateMultiplexer();
        var reader = new RpcTestEnvironment.ChannelLineReader();
        var writer = new RpcTestEnvironment.ChannelLineWriter();
        var run = multiplexer.RunAsync(reader, writer);
        await InitializeAsync(reader, writer);

        reader.Send("{\"id\":2,\"method\":\"thread/start\",\"params\":{\"cwd\":\"C:/work\"}}");
        using var start = await ReadResponseByIdAsync(writer, "2");
        Assert.True(start.RootElement.TryGetProperty("result", out var startResult));
        var threadId = startResult.GetProperty("thread").GetProperty("id").GetString()!;
        Assert.StartsWith("thread-a-", threadId, StringComparison.Ordinal);
        Assert.Equal(RouteReason.Recovery, (await env.Repository.GetThreadRouteAsync(new ThreadId(threadId)))!.Reason);
        Assert.DoesNotContain("thread/start", env.Factory.Configure(new AccountId("b")).Calls);

        reader.Send("{\"id\":3,\"method\":\"thread/list\",\"params\":{\"limit\":20,\"sortKey\":\"updated_at\"}}");
        using var list = await ReadResponseByIdAsync(writer, "3");
        var ids = list.RootElement.GetProperty("result").GetProperty("data").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()).ToArray();
        Assert.Contains("a-list", ids);
        Assert.DoesNotContain("b-list", ids);

        reader.Complete();
        await run;
    }

    [Fact]
    public async Task Non_idempotent_thread_start_never_uses_overload_retry_path()
    {
        await using var env = await RpcTestEnvironment.CreateAsync();
        var profile = await env.AddAccountAsync("a", 10);
        env.Factory.Configure(profile.Id).OverloadThreadStart = true;
        await using var multiplexer = env.CreateMultiplexer();
        var reader = new RpcTestEnvironment.ChannelLineReader();
        var writer = new RpcTestEnvironment.ChannelLineWriter();
        var run = multiplexer.RunAsync(reader, writer);
        await InitializeAsync(reader, writer);

        reader.Send("{\"id\":2,\"method\":\"thread/start\",\"params\":{\"cwd\":\"C:/work\"}}");
        using var response = await ReadResponseByIdAsync(writer, "2");

        Assert.Equal(-32001, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(1, env.Factory.Configure(profile.Id).Calls.Count(method => method == "thread/start"));
        Assert.DoesNotContain("thread/start", env.Factory.Configure(profile.Id).RetryableCalls);

        reader.Complete();
        await run;
    }

    [Fact]
    public async Task Malformed_front_json_and_pre_handshake_request_fail_predictably()
    {
        await using var env = await RpcTestEnvironment.CreateAsync();
        await env.AddAccountAsync("a", 10);
        await using var multiplexer = env.CreateMultiplexer();
        var reader = new RpcTestEnvironment.ChannelLineReader();
        var writer = new RpcTestEnvironment.ChannelLineWriter();
        var run = multiplexer.RunAsync(reader, writer);

        reader.Send("{broken-json");
        using var parseError = await ReadAnyAsync(writer);
        Assert.Equal(-32700, parseError.RootElement.GetProperty("error").GetProperty("code").GetInt32());

        reader.Send("{\"id\":9,\"method\":\"account/read\",\"params\":{}}");
        using var handshakeError = await ReadResponseByIdAsync(writer, "9");
        Assert.Equal(-32002, handshakeError.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(
            "initialize must be called first",
            handshakeError.RootElement.GetProperty("error").GetProperty("message").GetString());

        reader.Complete();
        await run;
    }

    [Fact]
    public async Task Desktop_login_after_initialize_does_not_require_initialized_notification()
    {
        await using var env = await RpcTestEnvironment.CreateAsync();
        await env.AddAccountAsync("a", 10);
        await using var multiplexer = env.CreateMultiplexer();
        var reader = new RpcTestEnvironment.ChannelLineReader();
        var writer = new RpcTestEnvironment.ChannelLineWriter();
        var run = multiplexer.RunAsync(reader, writer);

        reader.Send("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codex-desktop\",\"version\":\"1\"},\"capabilities\":{\"experimentalApi\":false}}}");
        _ = await ReadResponseByIdAsync(writer, "1");

        reader.Send("{\"id\":2,\"method\":\"account/login/start\",\"params\":{\"type\":\"chatgpt\"}}");
        using var loginStart = await ReadResponseByIdAsync(writer, "2");
        Assert.True(loginStart.RootElement.TryGetProperty("result", out _));
        Assert.False(loginStart.RootElement.TryGetProperty("error", out _));
        Assert.Contains("account/login/start", env.Factory.Configure(new AccountId("a")).Calls);

        reader.Send("{\"id\":3,\"method\":\"account/read\",\"params\":{}}");
        using var accountRead = await ReadResponseByIdAsync(writer, "3");
        Assert.True(accountRead.RootElement.TryGetProperty("result", out _));
        Assert.False(accountRead.RootElement.TryGetProperty("error", out _));

        reader.Complete();
        await run;
    }

    [Fact]
    public async Task Front_initialize_capability_is_captured_before_workers_are_created()
    {
        await using var env = await RpcTestEnvironment.CreateAsync();
        await env.AddAccountAsync("a", 10);
        var context = new WorkerClientContext();
        await using var multiplexer = env.CreateMultiplexer(context);
        var reader = new RpcTestEnvironment.ChannelLineReader();
        var writer = new RpcTestEnvironment.ChannelLineWriter();
        var run = multiplexer.RunAsync(reader, writer);

        reader.Send("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"},\"capabilities\":{\"experimentalApi\":true}}}");
        _ = await ReadResponseByIdAsync(writer, "1");
        reader.Send("{\"method\":\"initialized\"}");

        Assert.True(context.Apply(new WorkerStartOptions()).ExperimentalApi);

        reader.Complete();
        await run;
    }

    [Fact]
    public async Task Front_initialize_experimentalApi_false_does_not_disable_worker_experimental_api()
    {
        await using var env = await RpcTestEnvironment.CreateAsync();
        await env.AddAccountAsync("a", 10);
        var context = new WorkerClientContext();
        await using var multiplexer = env.CreateMultiplexer(context);
        var reader = new RpcTestEnvironment.ChannelLineReader();
        var writer = new RpcTestEnvironment.ChannelLineWriter();
        var run = multiplexer.RunAsync(reader, writer);

        reader.Send("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codex-desktop\",\"version\":\"1\"},\"capabilities\":{\"experimentalApi\":false}}}");
        _ = await ReadResponseByIdAsync(writer, "1");

        Assert.True(context.Apply(new WorkerStartOptions()).ExperimentalApi);

        reader.Complete();
        await run;
    }

    private static async Task InitializeAsync(
        RpcTestEnvironment.ChannelLineReader reader,
        RpcTestEnvironment.ChannelLineWriter writer)
    {
        reader.Send("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"},\"capabilities\":{\"experimentalApi\":false}}}");
        using var response = await ReadResponseByIdAsync(writer, "1");
        Assert.Equal("codex-router/0.1.0", response.RootElement.GetProperty("result").GetProperty("userAgent").GetString());
        reader.Send("{\"method\":\"initialized\"}");
    }

    private static async Task<JsonDocument> ReadResponseByIdAsync(
        RpcTestEnvironment.ChannelLineWriter writer,
        string id,
        int maxLines = 30)
    {
        for (var i = 0; i < maxLines; i++)
        {
            var line = await writer.ReadNextAsync(TimeSpan.FromSeconds(5));
            var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("id", out var idElement) && IdText(idElement) == id)
            {
                return document;
            }
            document.Dispose();
        }
        throw new TimeoutException($"No front response with id '{id}' was observed.");
    }

    private static async Task<JsonDocument> ReadMethodAsync(
        RpcTestEnvironment.ChannelLineWriter writer,
        string method,
        int maxLines = 30)
    {
        for (var i = 0; i < maxLines; i++)
        {
            var line = await writer.ReadNextAsync(TimeSpan.FromSeconds(5));
            var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("method", out var methodElement) &&
                methodElement.ValueKind == JsonValueKind.String &&
                methodElement.GetString() == method)
            {
                return document;
            }
            document.Dispose();
        }
        throw new TimeoutException($"No front message with method '{method}' was observed.");
    }

    private static async Task<JsonDocument> ReadAnyAsync(RpcTestEnvironment.ChannelLineWriter writer) =>
        JsonDocument.Parse(await writer.ReadNextAsync(TimeSpan.FromSeconds(5)));

    private static string IdText(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => id.GetString() ?? string.Empty,
        JsonValueKind.Number => id.GetRawText(),
        _ => string.Empty
    };

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        Assert.True(predicate(), "Condition was not satisfied before timeout.");
    }
}
