using CodexRouter.Domain;

namespace CodexRouter.Storage;

public enum AccountLifecycle
{
    Pending,
    Active
}

public sealed record StoredAccount(
    AccountProfile Profile,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    AccountLifecycle Lifecycle = AccountLifecycle.Active);

public sealed record AccountPreferences(
    AccountId AccountId,
    double RouteWeight,
    int ShortReservePercent,
    int LongReservePercent,
    DateTimeOffset UpdatedAt);

public sealed record RouterSettings(
    RouterMode Mode,
    AccountId? PinnedAccountId,
    int ShortReservePercent,
    int LongReservePercent,
    TimeSpan QuotaStaleAfter,
    TimeSpan WorkerIdleTimeout,
    DateTimeOffset UpdatedAt)
{
    public static RouterSettings Default => new(
        RouterMode.Auto,
        null,
        15,
        8,
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        DateTimeOffset.UnixEpoch);
}

public sealed record HealthEventRecord(
    long Id,
    AccountHealth Health,
    DateTimeOffset CreatedAt);

public sealed record WorkerSessionRecord(
    long Id,
    WorkerId WorkerId,
    AccountId AccountId,
    WorkerState State,
    int? ProcessId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int? ExitCode,
    string? Failure);

public sealed record CompatibilityRunRecord(long Id, CompatibilityReport Report);

public enum MigrationJobStatus
{
    Pending,
    Snapshotting,
    CreatingDestination,
    Linking,
    Completed,
    Failed,
    Cancelled
}

public sealed record MigrationJobRecord(
    string Id,
    ThreadId SourceThreadId,
    AccountId SourceAccountId,
    AccountId DestinationAccountId,
    ThreadId? DestinationThreadId,
    MigrationJobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Failure);
