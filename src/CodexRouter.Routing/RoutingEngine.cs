using CodexRouter.Domain;

namespace CodexRouter.Routing;

public sealed class RoutingEngine
{
    private readonly RoutingPolicy _policy;

    public RoutingEngine(RoutingPolicy? policy = null)
    {
        _policy = policy ?? new RoutingPolicy();
        ValidatePolicy(_policy);
    }

    public RouteSelection Select(
        IReadOnlyList<RouteCandidateSnapshot> candidates,
        AccountId? pinnedAccount = null,
        RouteReason reason = RouteReason.AutoQuota,
        RouteRequestContext? requestContext = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var explanations = candidates
            .Select(candidate => EvaluateCandidate(candidate, requestContext))
            .OrderBy(static candidate => candidate.AccountId.Value, StringComparer.Ordinal)
            .ToArray();

        if (pinnedAccount is { } pinned)
        {
            var pinnedExplanation = explanations.FirstOrDefault(candidate => candidate.AccountId == pinned);
            if (pinnedExplanation is null)
            {
                throw new PinnedAccountUnavailableException(pinned, new[] { "account not found" });
            }
            if (!pinnedExplanation.Eligible)
            {
                throw new PinnedAccountUnavailableException(pinned, pinnedExplanation.RejectedReasons);
            }
            return BuildSelection(pinnedExplanation, reason, explanations);
        }

        var eligible = explanations
            .Where(static candidate => candidate.Eligible)
            .OrderByDescending(static candidate => candidate.Score)
            .ThenByDescending(candidate => candidates.First(snapshot => snapshot.Profile.Id == candidate.AccountId).Profile.Priority)
            .ThenBy(static candidate => candidate.AccountId.Value, StringComparer.Ordinal)
            .ToArray();
        if (eligible.Length == 0)
        {
            throw new NoEligibleAccountException(explanations);
        }

        return BuildSelection(eligible[0], reason, explanations);
    }

    public CandidateRouteExplanation EvaluateCandidate(
        RouteCandidateSnapshot candidate,
        RouteRequestContext? requestContext = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var rejected = new List<string>();
        if (!candidate.Profile.Enabled)
        {
            rejected.Add("disabled");
        }
        if (candidate.Compatibility is not (CompatibilityState.Compatible or CompatibilityState.Degraded))
        {
            rejected.Add($"compatibility:{candidate.Compatibility.ToString().ToLowerInvariant()}");
        }

        if (candidate.Health is { } health)
        {
            switch (health.State)
            {
                case AccountHealthState.Disabled:
                    rejected.Add("health:disabled");
                    break;
                case AccountHealthState.AuthRequired:
                    rejected.Add("health:auth-required");
                    break;
                case AccountHealthState.Cooldown:
                    rejected.Add("health:cooldown");
                    break;
                case AccountHealthState.Draining:
                    rejected.Add("health:draining-new-thread");
                    break;
            }
        }

        var quotaAge = candidate.Quota is null ? (TimeSpan?)null : candidate.Now - candidate.Quota.FetchedAt;
        var quotaStale = candidate.Quota is null || quotaAge > candidate.QuotaStaleAfter;
        if (candidate.Quota is null)
        {
            rejected.Add("quota:missing");
        }
        else if (candidate.Quota.Buckets.Count == 0)
        {
            // An empty successful protocol response is not evidence of 100%
            // headroom. Keep the account out of automatic routing until a
            // usable full read or sparse update is available.
            rejected.Add("quota:empty");
        }
        else if (quotaStale)
        {
            rejected.Add($"quota:stale:{quotaAge!.Value.TotalSeconds:0}s");
        }

        var factors = ScoreFactors(candidate, requestContext);
        var factorSum = factors.Sum(static factor => factor.Contribution);
        var routeWeight = Math.Clamp(candidate.RouteWeight, 0.0, 3.0);
        var score = factorSum * routeWeight;

        return new CandidateRouteExplanation(
            candidate.Profile.Id,
            rejected.Count == 0,
            rejected,
            score,
            factors,
            candidate.Quota?.FetchedAt,
            quotaStale,
            Math.Max(0, candidate.ActiveLoad),
            Math.Max(0, candidate.RecentFailures));
    }

    private IReadOnlyList<RouteFactorScore> ScoreFactors(
        RouteCandidateSnapshot candidate,
        RouteRequestContext? requestContext)
    {
        var weights = _policy.EffectiveWeights;
        var quota = candidate.Quota;
        var applicableBuckets = quota is null
            ? Array.Empty<QuotaBucket>()
            : SelectApplicableBuckets(quota.Buckets, requestContext);
        var shortBuckets = applicableBuckets
            .Where(bucket => bucket.WindowDuration is { } duration && duration <= _policy.EffectiveShortWindowBoundary)
            .ToArray() ?? Array.Empty<QuotaBucket>();
        var longBuckets = applicableBuckets
            .Where(bucket => bucket.WindowDuration is { } duration && duration > _policy.EffectiveShortWindowBoundary)
            .ToArray() ?? Array.Empty<QuotaBucket>();

        var raw = new List<(string Name, double? Value, double Weight, string? Note)>
        {
            ("short_headroom", BottleneckRemaining(shortBuckets), weights.ShortHeadroom, shortBuckets.Length == 0 ? "no short-duration bucket" : null),
            ("long_headroom", BottleneckRemaining(longBuckets), weights.LongHeadroom, longBuckets.Length == 0 ? "no long-duration bucket" : null),
            ("general_headroom", applicableBuckets.Count > 0 ? BottleneckRemaining(applicableBuckets) : null,
                weights.GeneralHeadroom, applicableBuckets.Count > 0 ? null : "no applicable quota bucket"),
            ("reset_opportunity", ComputeResetOpportunity(candidate, requestContext), weights.ResetOpportunity, null),
            ("health_confidence", HealthConfidence(candidate.Health), weights.HealthConfidence, candidate.Health?.State.ToString()),
            ("active_load", 100.0 - Math.Min(100.0, Math.Max(0, candidate.ActiveLoad) * 20.0), weights.ActiveLoad, $"active={Math.Max(0, candidate.ActiveLoad)}"),
            ("recent_failures", 100.0 - Math.Min(100.0, Math.Max(0, candidate.RecentFailures) * 25.0), weights.RecentFailures, $"failures={Math.Max(0, candidate.RecentFailures)}"),
            ("user_priority", Math.Clamp(50.0 + candidate.Profile.Priority * 5.0, 0.0, 100.0), weights.UserPriority,
                $"priority={candidate.Profile.Priority}; routeWeight={candidate.RouteWeight:0.###}")
        };

        var availableWeight = raw.Where(static factor => factor.Value is not null && factor.Weight > 0)
            .Sum(static factor => factor.Weight);
        if (availableWeight <= 0)
        {
            return raw.Select(static factor => new RouteFactorScore(factor.Name, factor.Value, factor.Weight, 0, 0, factor.Note)).ToArray();
        }

        return raw.Select(factor =>
        {
            if (factor.Value is null || factor.Weight <= 0)
            {
                return new RouteFactorScore(factor.Name, factor.Value, factor.Weight, 0, 0, factor.Note);
            }
            var effective = factor.Weight / availableWeight;
            return new RouteFactorScore(factor.Name, factor.Value, factor.Weight, effective, factor.Value.Value * effective, factor.Note);
        }).ToArray();
    }

    private double ComputeResetOpportunity(
        RouteCandidateSnapshot candidate,
        RouteRequestContext? requestContext)
    {
        if (candidate.Quota is null)
        {
            return 0;
        }

        var best = 0.0;
        foreach (var bucket in SelectApplicableBuckets(candidate.Quota.Buckets, requestContext))
        {
            if (bucket.ResetsAt is null)
            {
                continue;
            }
            var reserve = bucket.WindowDuration is { } duration && duration > _policy.EffectiveShortWindowBoundary
                ? _policy.LongReservePercent
                : _policy.ShortReservePercent;
            if (bucket.RemainingPercent <= reserve)
            {
                continue;
            }

            var untilReset = bucket.ResetsAt.Value - candidate.Now;
            if (untilReset <= TimeSpan.Zero || untilReset > _policy.EffectiveResetSoon)
            {
                continue;
            }
            if (untilReset <= _policy.EffectiveResetVerySoon)
            {
                best = Math.Max(best, 100.0);
                continue;
            }

            var span = (_policy.EffectiveResetSoon - _policy.EffectiveResetVerySoon).TotalSeconds;
            var progress = span <= 0
                ? 0
                : 1.0 - (untilReset - _policy.EffectiveResetVerySoon).TotalSeconds / span;
            best = Math.Max(best, 20.0 + Math.Clamp(progress, 0.0, 1.0) * 80.0);
        }
        return best;
    }

    private static double? BottleneckRemaining(IReadOnlyList<QuotaBucket> buckets) =>
        buckets.Count == 0 ? null : buckets.Min(static bucket => (double)bucket.RemainingPercent);

    private static IReadOnlyList<QuotaBucket> SelectApplicableBuckets(
        IReadOnlyList<QuotaBucket> buckets,
        RouteRequestContext? requestContext)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        if (buckets.Count == 0)
        {
            return Array.Empty<QuotaBucket>();
        }

        var general = buckets.Where(IsGeneralLimit).ToArray();

        // No model context means we cannot prove which named limit applies.
        // The account-wide Codex bucket is the safe default; unrelated named
        // limits belong to other products/models and must not block a normal
        // Codex thread. If the server omitted the general bucket, retain the
        // conservative all-bucket fallback.
        if (requestContext is null ||
            (string.IsNullOrWhiteSpace(requestContext.Model) && string.IsNullOrWhiteSpace(requestContext.LimitId)))
        {
            return general.Length > 0 ? general : buckets;
        }

        var explicitLimitId = Normalize(requestContext.LimitId);
        if (!string.IsNullOrWhiteSpace(explicitLimitId))
        {
            var explicitMatches = buckets
                .Where(bucket => string.Equals(Normalize(bucket.LimitId), explicitLimitId, StringComparison.Ordinal))
                .ToArray();
            if (explicitMatches.Length > 0)
            {
                return IncludeGeneralLimits(general, explicitMatches);
            }
        }

        var model = Normalize(requestContext.Model);
        if (!string.IsNullOrWhiteSpace(model))
        {
            var modelMatches = buckets
                .Where(bucket => !IsGeneralLimit(bucket) && MatchesModel(bucket, model))
                .ToArray();
            if (modelMatches.Length > 0)
            {
                // The general Codex limit is a global cap and must be applied
                // together with a matching model-specific cap.
                return IncludeGeneralLimits(general, modelMatches);
            }
        }

        // A known model with no matching named bucket falls back to the global
        // Codex limit. If the server omitted that legacy/general bucket, use
        // all available buckets conservatively rather than returning an
        // optimistic score.
        return general.Length > 0 ? general : buckets;
    }

    private static IReadOnlyList<QuotaBucket> IncludeGeneralLimits(
        IReadOnlyList<QuotaBucket> general,
        IReadOnlyList<QuotaBucket> specific)
    {
        if (general.Count == 0)
        {
            return specific;
        }

        return general.Concat(specific)
            .GroupBy(static bucket => (bucket.LimitId, bucket.Slot))
            .Select(static group => group.First())
            .ToArray();
    }

    private static bool IsGeneralLimit(QuotaBucket bucket)
    {
        var id = Normalize(bucket.LimitId);
        return id is "codex" or "default" or "";
    }

    private static bool MatchesModel(QuotaBucket bucket, string normalizedModel)
    {
        var limitId = Normalize(bucket.LimitId);
        var limitName = Normalize(bucket.LimitName);
        return IsSpecificMatch(limitId, normalizedModel) || IsSpecificMatch(limitName, normalizedModel);
    }

    private static bool IsSpecificMatch(string value, string model) =>
        value.Length >= 4 && string.Equals(value, model, StringComparison.Ordinal);

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static double HealthConfidence(AccountHealth? health) => health?.State switch
    {
        AccountHealthState.Healthy => 100,
        AccountHealthState.Degraded => 35,
        AccountHealthState.Unknown => 25,
        AccountHealthState.Draining => 20,
        AccountHealthState.Cooldown => 0,
        AccountHealthState.AuthRequired => 0,
        AccountHealthState.Disabled => 0,
        _ => 25
    };

    private static RouteSelection BuildSelection(
        CandidateRouteExplanation winner,
        RouteReason reason,
        IReadOnlyList<CandidateRouteExplanation> candidates) =>
        new(
            winner.AccountId,
            reason,
            winner.Score,
            DateTimeOffset.UtcNow,
            candidates,
            Guid.NewGuid().ToString("N"));

    private static void ValidatePolicy(RoutingPolicy policy)
    {
        var weights = policy.EffectiveWeights;
        var all = new[]
        {
            weights.ShortHeadroom, weights.LongHeadroom, weights.GeneralHeadroom, weights.ResetOpportunity,
            weights.HealthConfidence, weights.ActiveLoad, weights.RecentFailures, weights.UserPriority
        };
        if (all.Any(static weight => weight < 0) || all.Sum() <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Routing weights must be non-negative and at least one must be positive.");
        }
        if (policy.ShortReservePercent is < 0 or > 100 || policy.LongReservePercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Safety reserves must be between 0 and 100.");
        }
    }
}
