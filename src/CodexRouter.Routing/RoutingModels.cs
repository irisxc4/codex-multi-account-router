using CodexRouter.Domain;

namespace CodexRouter.Routing;

public sealed record RoutingWeights(
    double ShortHeadroom = 0.30,
    double LongHeadroom = 0.25,
    double GeneralHeadroom = 0.15,
    double ResetOpportunity = 0.10,
    double HealthConfidence = 0.05,
    double ActiveLoad = 0.05,
    double RecentFailures = 0.05,
    double UserPriority = 0.05);

public sealed record RoutingPolicy(
    RoutingWeights? Weights = null,
    TimeSpan? ShortWindowBoundary = null,
    TimeSpan? ResetSoon = null,
    TimeSpan? ResetVerySoon = null,
    int ShortReservePercent = 15,
    int LongReservePercent = 8)
{
    public RoutingWeights EffectiveWeights => Weights ?? new RoutingWeights();
    public TimeSpan EffectiveShortWindowBoundary => ShortWindowBoundary ?? TimeSpan.FromDays(1);
    public TimeSpan EffectiveResetSoon => ResetSoon ?? TimeSpan.FromHours(2);
    public TimeSpan EffectiveResetVerySoon => ResetVerySoon ?? TimeSpan.FromMinutes(30);
}

/// <summary>
/// The quota context of a request that is about to create a new thread.
/// LimitId is an explicit server-side quota identifier when one is available;
/// Model is used to match model-specific limit names/ids otherwise.
/// </summary>
public sealed record RouteRequestContext(
    string? Model = null,
    string? LimitId = null,
    string? ModelProvider = null);

/// <summary>
/// Allows the RPC boundary to perform a stale-on-demand quota prefetch before
/// the coordinator evaluates candidates. The routing assembly deliberately
/// depends on this small contract instead of a concrete account service.
/// </summary>
public interface IQuotaFreshnessProvider
{
    Task RefreshStaleAsync(
        TimeSpan staleAfter,
        int shortReservePercent = 15,
        int longReservePercent = 8,
        CancellationToken cancellationToken = default);
}

public sealed record RouteCandidateSnapshot(
    AccountProfile Profile,
    AccountHealth? Health,
    QuotaSnapshot? Quota,
    DateTimeOffset Now,
    TimeSpan QuotaStaleAfter,
    CompatibilityState Compatibility,
    int ActiveLoad,
    int RecentFailures,
    double RouteWeight = 1.0);

public sealed record RouteFactorScore(
    string Name,
    double? RawValue,
    double ConfiguredWeight,
    double EffectiveWeight,
    double Contribution,
    string? Note = null);

public sealed record CandidateRouteExplanation(
    AccountId AccountId,
    bool Eligible,
    IReadOnlyList<string> RejectedReasons,
    double Score,
    IReadOnlyList<RouteFactorScore> Factors,
    DateTimeOffset? QuotaFetchedAt,
    bool QuotaStale,
    int ActiveLoad,
    int RecentFailures);

public sealed record RouteSelection(
    AccountId AccountId,
    RouteReason Reason,
    double Score,
    DateTimeOffset DecidedAt,
    IReadOnlyList<CandidateRouteExplanation> Candidates,
    string DecisionId)
{
    public CandidateRouteExplanation Winner =>
        Candidates.First(candidate => candidate.AccountId == AccountId);
}

public sealed record TemporaryPin(AccountId AccountId, DateTimeOffset ExpiresAt);

public sealed class NoEligibleAccountException : Exception
{
    public NoEligibleAccountException(IReadOnlyList<CandidateRouteExplanation> candidates)
        : base("No account is eligible for a new thread.") => Candidates = candidates;

    public IReadOnlyList<CandidateRouteExplanation> Candidates { get; }
}

public sealed class RoutingDisabledException : Exception
{
    public RoutingDisabledException() : base("Router mode is Off; automatic account selection is disabled.") { }
}

public sealed class PinnedAccountUnavailableException : Exception
{
    public PinnedAccountUnavailableException(AccountId accountId, IReadOnlyList<string> reasons)
        : base($"Pinned account '{accountId}' is not eligible: {string.Join(", ", reasons)}")
    {
        AccountId = accountId;
        Reasons = reasons;
    }

    public AccountId AccountId { get; }
    public IReadOnlyList<string> Reasons { get; }
}
