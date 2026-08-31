using CodexRouter.Domain;
using CodexRouter.Protocol;
using Xunit;

namespace CodexRouter.Protocol.Tests;

public sealed class ProtocolAdapterTests
{
    private readonly CodexProtocolAdapter _adapter = new();
    private static readonly AccountId Account = new("account-a");

    [Fact]
    public void Account_read_ignores_unknown_fields()
    {
        var result = _adapter.MapAccountRead(Account, Fixture("account-read-chatgpt.json"));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Value);
        Assert.Equal(AccountAuthKind.ChatGpt, result.Value!.AuthKind);
        Assert.Equal("router@example.com", result.Value.Email);
        Assert.Equal("plus", result.Value.PlanType);
        Assert.True(result.Value.RequiresOpenAiAuth);
    }

    [Fact]
    public void Legacy_rate_limits_map_to_stable_buckets()
    {
        var result = _adapter.MapRateLimitsRead(Account, Fixture("rate-limits-legacy.json"));

        Assert.True(result.Succeeded);
        var snapshot = Assert.IsType<QuotaSnapshot>(result.Value);
        Assert.Equal(2, snapshot.Buckets.Count);
        Assert.Equal(42, snapshot.Buckets.Single(bucket => bucket.Slot == QuotaBucketSlot.Primary).RemainingPercent);
        Assert.Equal(TimeSpan.FromMinutes(10080), snapshot.Buckets.Single(bucket => bucket.Slot == QuotaBucketSlot.Secondary).WindowDuration);
        Assert.Equal("12.50", snapshot.CreditBalance);
        Assert.Equal("plus", snapshot.PlanType);
    }

    [Fact]
    public void Multi_limit_view_wins_over_backward_compatible_snapshot()
    {
        var result = _adapter.MapRateLimitsRead(Account, Fixture("rate-limits-multi.json"));

        Assert.True(result.Succeeded);
        var snapshot = Assert.IsType<QuotaSnapshot>(result.Value);
        Assert.Equal(3, snapshot.Buckets.Count);
        Assert.DoesNotContain(snapshot.Buckets, bucket => bucket.UsedPercent == 99);
        Assert.Contains(snapshot.Buckets, bucket => bucket.LimitId == "codex" && bucket.UsedPercent == 12);
        Assert.Contains(snapshot.Buckets, bucket => bucket.LimitId == "review" && bucket.UsedPercent == 73);
        Assert.True(snapshot.SpendControlReached);
        Assert.Equal("workspace_member_usage_limit_reached", snapshot.RateLimitReachedType);
    }

    [Fact]
    public void Legacy_global_limit_is_merged_when_multi_view_only_contains_special_limits()
    {
        const string json = """
        {"rateLimits":{"limitId":"codex","primary":{"usedPercent":33,"windowDurationMins":300},"planType":"plus"},"rateLimitsByLimitId":{"codex_bengalfox":{"limitId":"codex_bengalfox","limitName":"GPT-5.3-Codex-Spark","primary":{"usedPercent":7,"windowDurationMins":300}}}}
        """;

        var result = _adapter.MapRateLimitsRead(Account, json);

        Assert.True(result.Succeeded);
        var snapshot = Assert.IsType<QuotaSnapshot>(result.Value);
        Assert.Equal(2, snapshot.Buckets.Count);
        Assert.Contains(snapshot.Buckets, bucket => bucket.LimitId == "codex" && bucket.UsedPercent == 33);
        Assert.Contains(snapshot.Buckets, bucket => bucket.LimitId == "codex_bengalfox" && bucket.UsedPercent == 7);
        Assert.Equal("plus", snapshot.PlanType);
    }

    [Fact]
    public void Empty_multi_update_falls_back_to_legacy_snapshot()
    {
        const string json = """
        {"rateLimitsByLimitId":{},"rateLimits":{"limitId":"codex","primary":{"usedPercent":44,"windowDurationMins":300}}}
        """;

        var result = _adapter.MapRateLimitsUpdated(Account, json);

        Assert.True(result.Succeeded);
        var patch = Assert.Single(result.Value!.Buckets);
        Assert.Equal("codex", patch.LimitId);
        Assert.Equal(44, patch.UsedPercent.Value);
    }

    [Fact]
    public void Sparse_rate_limit_update_preserves_missing_and_distinguishes_null()
    {
        const string json = """
        {"rateLimits":{"limitId":"codex","limitName":null,"primary":{"usedPercent":61,"windowDurationMins":null,"resetsAt":1786845600},"planType":null,"spendControlReached":true,"credits":null}}
        """;

        var result = _adapter.MapRateLimitsUpdated(Account, json);

        Assert.True(result.Succeeded);
        var update = Assert.IsType<QuotaSparseUpdate>(result.Value);
        var primary = Assert.Single(update.Buckets);
        Assert.Equal("codex", primary.LimitId);
        Assert.True(primary.UsedPercent.IsPresent);
        Assert.True(primary.UsedPercent.HasValue);
        Assert.Equal(61, primary.UsedPercent.Value);
        Assert.True(primary.WindowDuration.IsPresent);
        Assert.False(primary.WindowDuration.HasValue);
        Assert.True(update.PlanType.IsPresent);
        Assert.False(update.PlanType.HasValue);
        Assert.True(update.SpendControlReached.HasValue);
        Assert.True(update.SpendControlReached.Value);
        Assert.True(update.HasCredits.IsPresent);
        Assert.False(update.HasCredits.HasValue);
        Assert.False(update.RateLimitReachedType.IsPresent);
    }

    [Fact]
    public void Usage_mapping_drops_malformed_optional_daily_rows()
    {
        var result = _adapter.MapUsageRead(Account, Fixture("usage.json"));

        Assert.True(result.Succeeded);
        var usage = Assert.IsType<UsageSnapshot>(result.Value);
        Assert.Equal(72700, usage.LifetimeTokens);
        Assert.Equal(2, usage.DailyBuckets.Count);
        Assert.Equal(new DateOnly(2026, 8, 16), usage.DailyBuckets[1].StartDate);
    }

    [Fact]
    public void Missing_optional_quota_fields_do_not_fail_mapping()
    {
        const string json = """
        {"result":{"rateLimits":{"primary":{"usedPercent":25},"secondary":null,"future":123}}}
        """;

        var result = _adapter.MapRateLimitsRead(Account, json);

        Assert.True(result.Succeeded);
        var bucket = Assert.Single(result.Value!.Buckets);
        Assert.Null(bucket.WindowDuration);
        Assert.Null(bucket.ResetsAt);
    }

    [Fact]
    public void Invalid_json_returns_error_instead_of_throwing()
    {
        var result = _adapter.MapRateLimitsRead(Account, "{broken");

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "protocol", name));
}
