using CodexRouter.Domain;
using Xunit;

namespace CodexRouter.Domain.Tests;

public sealed class DomainModelTests
{
    [Fact]
    public void StrongIds_reject_blank_values()
    {
        Assert.Throws<ArgumentException>(() => new AccountId("   "));
        Assert.Throws<ArgumentException>(() => new ThreadId(""));
        Assert.Throws<ArgumentException>(() => new WorkerId("\t"));
    }

    [Fact]
    public void QuotaBucket_clamps_usage_and_computes_remaining()
    {
        var high = new QuotaBucket("codex", null, QuotaBucketSlot.Primary, 150, TimeSpan.FromHours(5), null);
        var low = new QuotaBucket("codex", null, QuotaBucketSlot.Secondary, -10, null, null);

        Assert.Equal(100, high.UsedPercent);
        Assert.Equal(0, high.RemainingPercent);
        Assert.Equal(0, low.UsedPercent);
        Assert.Equal(100, low.RemainingPercent);
    }

    [Fact]
    public void QuotaSnapshot_reports_tightest_bucket()
    {
        var account = new AccountId("a");
        var snapshot = new QuotaSnapshot(account, new[]
        {
            new QuotaBucket("codex", "Codex", QuotaBucketSlot.Primary, 58, TimeSpan.FromHours(5), null),
            new QuotaBucket("codex", "Codex", QuotaBucketSlot.Secondary, 29, TimeSpan.FromDays(7), null)
        }, DateTimeOffset.UtcNow);

        Assert.Equal(42, snapshot.TightestRemainingPercent);
    }

    [Theory]
    [InlineData(CompatibilityState.Compatible, true)]
    [InlineData(CompatibilityState.Degraded, true)]
    [InlineData(CompatibilityState.Incompatible, false)]
    [InlineData(CompatibilityState.Unknown, false)]
    public void CompatibilityReport_controls_routing(CompatibilityState state, bool expected)
    {
        var report = new CompatibilityReport(
            state,
            null,
            null,
            DateTimeOffset.UtcNow,
            Array.Empty<CompatibilityIssue>(),
            Array.Empty<string>(),
            Array.Empty<string>());

        Assert.Equal(expected, report.RoutingAllowed);
    }
}
