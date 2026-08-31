using System.Collections.ObjectModel;

namespace CodexRouter.Domain;

public readonly record struct AccountId
{
    public AccountId(string value) => Value = Require(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;

    private static string Require(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Identifier cannot be empty.", parameterName)
            : value.Trim();
}

public readonly record struct ThreadId
{
    public ThreadId(string value) => Value = Require(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;

    private static string Require(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Identifier cannot be empty.", parameterName)
            : value.Trim();
}

public readonly record struct WorkerId
{
    public WorkerId(string value) => Value = Require(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;

    private static string Require(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Identifier cannot be empty.", parameterName)
            : value.Trim();
}

public sealed record AccountProfile(
    AccountId Id,
    string Alias,
    string CodexHome,
    string? Email = null,
    string? PlanType = null,
    bool Enabled = true,
    int Priority = 0)
{
    public string Alias { get; init; } = string.IsNullOrWhiteSpace(Alias)
        ? throw new ArgumentException("Account alias cannot be empty.", nameof(Alias))
        : Alias.Trim();

    public string CodexHome { get; init; } = string.IsNullOrWhiteSpace(CodexHome)
        ? throw new ArgumentException("CODEX_HOME cannot be empty.", nameof(CodexHome))
        : Path.GetFullPath(CodexHome);
}

public sealed record AccountObservation(
    AccountId AccountId,
    AccountAuthKind AuthKind,
    string? Email,
    string? PlanType,
    bool RequiresOpenAiAuth,
    DateTimeOffset ObservedAt);

public enum AccountAuthKind
{
    Unknown,
    None,
    ApiKey,
    ChatGpt,
    AmazonBedrock,
    PersonalAccessToken
}

public enum AccountHealthState
{
    Unknown,
    Healthy,
    Draining,
    Cooldown,
    AuthRequired,
    Degraded,
    Disabled
}

public sealed record AccountHealth(
    AccountId AccountId,
    AccountHealthState State,
    DateTimeOffset CheckedAt,
    string? Reason = null,
    DateTimeOffset? CooldownUntil = null)
{
    public bool AcceptsNewThreads => State == AccountHealthState.Healthy;
    public bool CanContinueExistingThreads => State is AccountHealthState.Healthy or AccountHealthState.Draining;
}

public enum QuotaBucketSlot
{
    Primary,
    Secondary,
    Other
}

public sealed record QuotaBucket(
    string LimitId,
    string? LimitName,
    QuotaBucketSlot Slot,
    int UsedPercent,
    TimeSpan? WindowDuration,
    DateTimeOffset? ResetsAt)
{
    public string LimitId { get; init; } = string.IsNullOrWhiteSpace(LimitId) ? "default" : LimitId.Trim();
    public int UsedPercent { get; init; } = Math.Clamp(UsedPercent, 0, 100);
    public int RemainingPercent => 100 - UsedPercent;
}

public readonly record struct OptionalPatch<T>(bool IsPresent, bool HasValue, T? Value)
{
    public static OptionalPatch<T> Missing => new(false, false, default);
    public static OptionalPatch<T> Null => new(true, false, default);
    public static OptionalPatch<T> Present(T value) => new(true, true, value);
}

public sealed record QuotaBucketPatch(
    string LimitId,
    QuotaBucketSlot Slot,
    OptionalPatch<string> LimitName,
    OptionalPatch<int> UsedPercent,
    OptionalPatch<TimeSpan> WindowDuration,
    OptionalPatch<DateTimeOffset> ResetsAt);

public sealed record QuotaSparseUpdate(
    AccountId AccountId,
    IReadOnlyList<QuotaBucketPatch> Buckets,
    OptionalPatch<string> PlanType,
    OptionalPatch<string> RateLimitReachedType,
    OptionalPatch<bool> SpendControlReached,
    OptionalPatch<bool> HasCredits,
    OptionalPatch<bool> UnlimitedCredits,
    OptionalPatch<string> CreditBalance,
    DateTimeOffset ReceivedAt);

public sealed record QuotaSnapshot(
    AccountId AccountId,
    IReadOnlyList<QuotaBucket> Buckets,
    DateTimeOffset FetchedAt,
    string? PlanType = null,
    string? RateLimitReachedType = null,
    bool? SpendControlReached = null,
    bool? HasCredits = null,
    bool? UnlimitedCredits = null,
    string? CreditBalance = null)
{
    public IReadOnlyList<QuotaBucket> Buckets { get; init; } =
        new ReadOnlyCollection<QuotaBucket>((Buckets ?? Array.Empty<QuotaBucket>()).ToArray());

    public int? TightestRemainingPercent => Buckets.Count == 0 ? null : Buckets.Min(static bucket => bucket.RemainingPercent);
    public bool IsRateLimited => !string.IsNullOrWhiteSpace(RateLimitReachedType);
}

public sealed record UsageDailyBucket(DateOnly StartDate, long Tokens);

public sealed record UsageSnapshot(
    AccountId AccountId,
    DateTimeOffset FetchedAt,
    long? LifetimeTokens,
    long? PeakDailyTokens,
    long? LongestRunningTurnSeconds,
    long? CurrentStreakDays,
    long? LongestStreakDays,
    IReadOnlyList<UsageDailyBucket> DailyBuckets)
{
    public IReadOnlyList<UsageDailyBucket> DailyBuckets { get; init; } =
        new ReadOnlyCollection<UsageDailyBucket>((DailyBuckets ?? Array.Empty<UsageDailyBucket>()).ToArray());
}

public enum RouteReason
{
    AutoQuota,
    ManualPin,
    Sticky,
    Recovery,
    Migration
}

public sealed record RouteScoreComponent(string Name, double Value, double Weight);

public sealed record RouteDecision(
    AccountId AccountId,
    RouteReason Reason,
    double Score,
    DateTimeOffset DecidedAt,
    IReadOnlyList<RouteScoreComponent> Components)
{
    public IReadOnlyList<RouteScoreComponent> Components { get; init; } =
        new ReadOnlyCollection<RouteScoreComponent>((Components ?? Array.Empty<RouteScoreComponent>()).ToArray());
}

public sealed record ThreadRoute(
    ThreadId ThreadId,
    AccountId AccountId,
    WorkerId WorkerId,
    RouteReason Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt);

public enum WorkerState
{
    Stopped,
    Starting,
    Initializing,
    Ready,
    Busy,
    Draining,
    Stopping,
    Crashed,
    Backoff,
    Quarantined,
    Failed
}

public enum RouterMode
{
    Auto,
    Pinned,
    Off
}

public enum CompatibilityState
{
    Unknown,
    Compatible,
    Degraded,
    Incompatible
}

public enum CompatibilityIssueSeverity
{
    Info,
    Warning,
    Error
}

public enum SchemaFlavor
{
    Stable,
    Experimental
}

public sealed record BinaryIdentity(
    string Path,
    string Version,
    string Sha256,
    long SizeBytes,
    DateTimeOffset LastWriteTimeUtc)
{
    public string Path { get; init; } = System.IO.Path.GetFullPath(Path);
}

public sealed record SchemaMetadata(
    SchemaFlavor Flavor,
    string DirectoryPath,
    string BinarySha256,
    string BinaryVersion,
    DateTimeOffset GeneratedAt,
    int SchemaFileCount,
    IReadOnlyList<string> Methods)
{
    public IReadOnlyList<string> Methods { get; init; } =
        new ReadOnlyCollection<string>((Methods ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray());
}

public sealed record CompatibilityIssue(
    string Code,
    CompatibilityIssueSeverity Severity,
    string Message,
    string? Method = null);

public sealed record CompatibilityReport(
    CompatibilityState State,
    BinaryIdentity? Binary,
    SchemaMetadata? StableSchema,
    DateTimeOffset CheckedAt,
    IReadOnlyList<CompatibilityIssue> Issues,
    IReadOnlyList<string> MissingRequiredMethods,
    IReadOnlyList<string> MissingOptionalMethods)
{
    public IReadOnlyList<CompatibilityIssue> Issues { get; init; } =
        new ReadOnlyCollection<CompatibilityIssue>((Issues ?? Array.Empty<CompatibilityIssue>()).ToArray());

    public IReadOnlyList<string> MissingRequiredMethods { get; init; } =
        new ReadOnlyCollection<string>((MissingRequiredMethods ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray());

    public IReadOnlyList<string> MissingOptionalMethods { get; init; } =
        new ReadOnlyCollection<string>((MissingOptionalMethods ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray());

    public bool RoutingAllowed => State is CompatibilityState.Compatible or CompatibilityState.Degraded;
    public bool AccountProbingAllowed => State is CompatibilityState.Compatible or CompatibilityState.Degraded;
    public bool PassThroughAllowed => Binary is not null;
}
