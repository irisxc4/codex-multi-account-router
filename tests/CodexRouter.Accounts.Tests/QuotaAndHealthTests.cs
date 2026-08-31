using CodexRouter.Accounts;
using CodexRouter.Domain;
using Xunit;

namespace CodexRouter.Accounts.Tests;

public sealed class QuotaAndHealthTests
{
    [Fact]
    public void Sparse_merge_updates_available_values_and_preserves_nullable_metadata()
    {
        var account = new AccountId("a");
        var baseline = new QuotaSnapshot(account, new[]
        {
            new QuotaBucket("codex", "Codex", QuotaBucketSlot.Primary, 40, TimeSpan.FromHours(5), DateTimeOffset.UtcNow.AddHours(2)),
            new QuotaBucket("codex", "Codex", QuotaBucketSlot.Secondary, 30, TimeSpan.FromDays(7), DateTimeOffset.UtcNow.AddDays(2))
        }, DateTimeOffset.UtcNow.AddMinutes(-1), "plus", null, false, true, false, "8.00");

        var update = new QuotaSparseUpdate(account, new[]
        {
            new QuotaBucketPatch(
                "codex",
                QuotaBucketSlot.Primary,
                OptionalPatch<string>.Null,
                OptionalPatch<int>.Present(61),
                OptionalPatch<TimeSpan>.Null,
                OptionalPatch<DateTimeOffset>.Missing),
            new QuotaBucketPatch(
                "review",
                QuotaBucketSlot.Primary,
                OptionalPatch<string>.Present("Review"),
                OptionalPatch<int>.Present(25),
                OptionalPatch<TimeSpan>.Present(TimeSpan.FromDays(1)),
                OptionalPatch<DateTimeOffset>.Missing)
        },
            OptionalPatch<string>.Null,
            OptionalPatch<string>.Missing,
            OptionalPatch<bool>.Present(true),
            OptionalPatch<bool>.Null,
            OptionalPatch<bool>.Missing,
            OptionalPatch<string>.Null,
            DateTimeOffset.UtcNow);

        var merged = new QuotaSnapshotMerger().Merge(baseline, update);

        Assert.Equal("plus", merged.PlanType);
        Assert.Equal("8.00", merged.CreditBalance);
        Assert.True(merged.HasCredits);
        Assert.True(merged.SpendControlReached);
        Assert.Equal(3, merged.Buckets.Count);
        var primary = merged.Buckets.Single(bucket => bucket.LimitId == "codex" && bucket.Slot == QuotaBucketSlot.Primary);
        Assert.Equal(61, primary.UsedPercent);
        Assert.Equal("Codex", primary.LimitName);
        Assert.Equal(TimeSpan.FromHours(5), primary.WindowDuration);
        Assert.Contains(merged.Buckets, bucket => bucket.LimitId == "review" && bucket.UsedPercent == 25);
    }

    [Fact]
    public void Health_state_machine_covers_disabled_auth_draining_cooldown_and_healthy()
    {
        var evaluator = new AccountHealthEvaluator();
        var id = new AccountId("a");
        var profile = new AccountProfile(id, "A", Path.Combine(Path.GetTempPath(), "profile-a"));
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(AccountHealthState.Disabled,
            evaluator.Evaluate(profile with { Enabled = false }, null, null, now).State);

        var auth = new AccountObservation(id, AccountAuthKind.None, null, null, true, now);
        Assert.Equal(AccountHealthState.AuthRequired,
            evaluator.Evaluate(profile, auth, null, now).State);

        var draining = new QuotaSnapshot(id, new[]
        {
            new QuotaBucket("codex", null, QuotaBucketSlot.Primary, 90, TimeSpan.FromHours(5), now.AddHours(1))
        }, now);
        Assert.Equal(AccountHealthState.Draining,
            evaluator.Evaluate(profile, null, draining, now, shortReservePercent: 15).State);

        var cooldown = draining with { RateLimitReachedType = "rate_limit_reached" };
        var cooldownHealth = evaluator.Evaluate(profile, null, cooldown, now);
        Assert.Equal(AccountHealthState.Cooldown, cooldownHealth.State);
        Assert.Equal(draining.Buckets[0].ResetsAt, cooldownHealth.CooldownUntil);

        var healthy = new QuotaSnapshot(id, new[]
        {
            new QuotaBucket("codex", null, QuotaBucketSlot.Primary, 25, TimeSpan.FromHours(5), now.AddHours(1))
        }, now);
        Assert.Equal(AccountHealthState.Healthy,
            evaluator.Evaluate(profile, null, healthy, now).State);
    }
}
