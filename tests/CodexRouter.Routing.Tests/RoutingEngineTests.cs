using CodexRouter.Domain;
using CodexRouter.Routing;
using Xunit;

namespace CodexRouter.Routing.Tests;

public sealed class RoutingEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Deterministic_tie_break_uses_account_id()
    {
        var engine = new RoutingEngine();
        var candidates = new[] { Candidate("b", 50, 50), Candidate("a", 50, 50) };

        var first = engine.Select(candidates);
        var second = engine.Select(candidates);

        Assert.Equal(new AccountId("a"), first.AccountId);
        Assert.Equal(first.AccountId, second.AccountId);
        Assert.Equal(first.Score, second.Score, 8);
    }

    [Fact]
    public void Missing_bucket_class_renormalizes_available_weights()
    {
        var engine = new RoutingEngine();
        var candidate = Candidate("a", shortUsed: null, longUsed: 20);

        var explanation = engine.EvaluateCandidate(candidate);
        var shortFactor = explanation.Factors.Single(factor => factor.Name == "short_headroom");

        Assert.Null(shortFactor.RawValue);
        Assert.Equal(0, shortFactor.EffectiveWeight);
        Assert.Equal(1.0, explanation.Factors.Sum(static factor => factor.EffectiveWeight), 8);
    }

    [Fact]
    public void Stale_disabled_auth_cooldown_and_draining_are_rejected_for_new_thread()
    {
        var engine = new RoutingEngine();
        var stale = Candidate("stale", 20, 20) with
        {
            Quota = Candidate("x", 20, 20).Quota! with { FetchedAt = Now.AddHours(-1) },
            QuotaStaleAfter = TimeSpan.FromMinutes(5)
        };
        var disabled = Candidate("disabled", 20, 20) with { Profile = Candidate("disabled", 20, 20).Profile with { Enabled = false } };
        var auth = Candidate("auth", 20, 20) with { Health = new AccountHealth(new AccountId("auth"), AccountHealthState.AuthRequired, Now) };
        var cooldown = Candidate("cool", 20, 20) with { Health = new AccountHealth(new AccountId("cool"), AccountHealthState.Cooldown, Now) };
        var draining = Candidate("drain", 20, 20) with { Health = new AccountHealth(new AccountId("drain"), AccountHealthState.Draining, Now) };

        Assert.False(engine.EvaluateCandidate(stale).Eligible);
        Assert.False(engine.EvaluateCandidate(disabled).Eligible);
        Assert.False(engine.EvaluateCandidate(auth).Eligible);
        Assert.False(engine.EvaluateCandidate(cooldown).Eligible);
        Assert.False(engine.EvaluateCandidate(draining).Eligible);
    }

    [Fact]
    public void Reset_opportunity_can_prefer_capacity_that_expires_soon()
    {
        var engine = new RoutingEngine(new RoutingPolicy(
            new RoutingWeights(
                ShortHeadroom: 0.15,
                LongHeadroom: 0,
                GeneralHeadroom: 0.10,
                ResetOpportunity: 0.70,
                HealthConfidence: 0.05,
                ActiveLoad: 0,
                RecentFailures: 0,
                UserPriority: 0)));

        var soon = Candidate("soon", 42, null, shortReset: Now.AddMinutes(20));
        var later = Candidate("later", 15, null, shortReset: Now.AddHours(4));
        var selection = engine.Select(new[] { soon, later });

        Assert.Equal(new AccountId("soon"), selection.AccountId);
    }

    [Fact]
    public void Reset_bonus_never_rewards_capacity_below_safety_reserve()
    {
        var engine = new RoutingEngine(new RoutingPolicy(ShortReservePercent: 15));
        var candidate = Candidate("a", 90, null, shortReset: Now.AddMinutes(10));

        var explanation = engine.EvaluateCandidate(candidate);
        var reset = explanation.Factors.Single(factor => factor.Name == "reset_opportunity");

        Assert.Equal(0, reset.RawValue);
    }

    [Fact]
    public void Manual_pin_selects_eligible_account_even_when_score_is_lower()
    {
        var engine = new RoutingEngine();
        var high = Candidate("high", 5, 5);
        var low = Candidate("low", 60, 60);

        var selection = engine.Select(new[] { high, low }, new AccountId("low"), RouteReason.ManualPin);

        Assert.Equal(new AccountId("low"), selection.AccountId);
        Assert.Equal(RouteReason.ManualPin, selection.Reason);
    }

    [Fact]
    public void Manual_pin_cannot_override_safety_filter()
    {
        var engine = new RoutingEngine();
        var disabled = Candidate("a", 10, 10) with { Profile = Candidate("a", 10, 10).Profile with { Enabled = false } };

        var error = Assert.Throws<PinnedAccountUnavailableException>(() =>
            engine.Select(new[] { disabled }, new AccountId("a"), RouteReason.ManualPin));

        Assert.Contains("disabled", error.Reasons);
    }

    [Fact]
    public void Route_weight_influences_score_without_changing_eligibility()
    {
        var engine = new RoutingEngine();
        var a = Candidate("a", 40, 40) with { RouteWeight = 0.5 };
        var b = Candidate("b", 40, 40) with { RouteWeight = 1.5 };

        var selection = engine.Select(new[] { a, b });

        Assert.Equal(new AccountId("b"), selection.AccountId);
        Assert.True(selection.Candidates.All(static candidate => candidate.Eligible));
    }

    [Fact]
    public void Model_specific_limit_is_combined_with_general_limit()
    {
        var engine = new RoutingEngine();
        var account = new AccountId("model-account");
        var quota = new QuotaSnapshot(account, new[]
        {
            new QuotaBucket("codex", "Codex", QuotaBucketSlot.Primary, 21,
                TimeSpan.FromHours(5), Now.AddHours(2)),
            new QuotaBucket("codex", "Codex", QuotaBucketSlot.Secondary, 21,
                TimeSpan.FromDays(7), Now.AddDays(3)),
            new QuotaBucket("codex_bengalfox", "GPT-5.3-Codex-Spark", QuotaBucketSlot.Primary, 0,
                TimeSpan.FromHours(5), Now.AddHours(2)),
            new QuotaBucket("codex_bengalfox", "GPT-5.3-Codex-Spark", QuotaBucketSlot.Secondary, 0,
                TimeSpan.FromDays(7), Now.AddDays(3))
        }, Now);
        var candidate = Candidate("model-account", 0, 0) with { Quota = quota };

        var explanation = engine.EvaluateCandidate(
            candidate,
            new RouteRequestContext(Model: "gpt-5.3-codex-spark"));

        Assert.Equal(79, explanation.Factors.Single(factor => factor.Name == "short_headroom").RawValue);
        Assert.Equal(79, explanation.Factors.Single(factor => factor.Name == "long_headroom").RawValue);
        Assert.Equal(79, explanation.Factors.Single(factor => factor.Name == "general_headroom").RawValue);
    }

    [Fact]
    public void Unknown_model_uses_general_codex_limit_without_unrelated_named_limits()
    {
        var engine = new RoutingEngine();
        var account = new AccountId("unknown-model");
        var quota = new QuotaSnapshot(account, new[]
        {
            new QuotaBucket("codex", "Codex", QuotaBucketSlot.Primary, 40,
                TimeSpan.FromHours(5), Now.AddHours(2)),
            new QuotaBucket("codex_bengalfox", "GPT-5.3-Codex-Spark", QuotaBucketSlot.Primary, 90,
                TimeSpan.FromHours(5), Now.AddHours(2))
        }, Now);
        var candidate = Candidate("unknown-model", 0, null) with { Quota = quota };

        var explanation = engine.EvaluateCandidate(candidate);

        Assert.Equal(60, explanation.Factors.Single(factor => factor.Name == "short_headroom").RawValue);
        Assert.Equal(60, explanation.Factors.Single(factor => factor.Name == "general_headroom").RawValue);
    }

    [Fact]
    public void Similar_but_different_model_does_not_match_named_limit()
    {
        var engine = new RoutingEngine();
        var account = new AccountId("exact-model");
        var quota = new QuotaSnapshot(account, new[]
        {
            new QuotaBucket("codex", "Codex", QuotaBucketSlot.Primary, 20,
                TimeSpan.FromHours(5), Now.AddHours(2)),
            new QuotaBucket("codex_bengalfox", "GPT-5.3-Codex-Spark", QuotaBucketSlot.Primary, 100,
                TimeSpan.FromHours(5), Now.AddHours(2))
        }, Now);
        var candidate = Candidate("exact-model", 0, null) with { Quota = quota };

        var explanation = engine.EvaluateCandidate(
            candidate,
            new RouteRequestContext(Model: "gpt-5.3-codex"));

        Assert.Equal(80, explanation.Factors.Single(factor => factor.Name == "short_headroom").RawValue);
    }

    private static RouteCandidateSnapshot Candidate(
        string id,
        int? shortUsed,
        int? longUsed,
        DateTimeOffset? shortReset = null)
    {
        var accountId = new AccountId(id);
        var buckets = new List<QuotaBucket>();
        if (shortUsed is not null)
        {
            buckets.Add(new QuotaBucket(
                "codex", "Codex", QuotaBucketSlot.Primary, shortUsed.Value,
                TimeSpan.FromHours(5), shortReset ?? Now.AddHours(3)));
        }
        if (longUsed is not null)
        {
            buckets.Add(new QuotaBucket(
                "codex", "Codex", QuotaBucketSlot.Secondary, longUsed.Value,
                TimeSpan.FromDays(7), Now.AddDays(3)));
        }

        return new RouteCandidateSnapshot(
            new AccountProfile(accountId, id.ToUpperInvariant(), Path.Combine(Path.GetTempPath(), "routing", id)),
            new AccountHealth(accountId, AccountHealthState.Healthy, Now),
            new QuotaSnapshot(accountId, buckets, Now),
            Now,
            TimeSpan.FromMinutes(5),
            CompatibilityState.Compatible,
            0,
            0,
            1.0);
    }
}
