using CodexRouter.Domain;

namespace CodexRouter.Control;

public sealed record ControlQuotaBucket(
    string LimitId,
    string? LimitName,
    string Slot,
    int UsedPercent,
    int RemainingPercent,
    double? WindowMinutes,
    DateTimeOffset? ResetsAt);

public sealed record ControlAccountView(
    string Id,
    string Alias,
    string? Email,
    string? PlanType,
    bool Enabled,
    int Priority,
    string Health,
    string? HealthReason,
    bool IsCurrent,
    DateTimeOffset? QuotaFetchedAt,
    IReadOnlyList<ControlQuotaBucket> QuotaBuckets);

public sealed record ControlSnapshot(
    string RouterMode,
    string? PinnedAccountId,
    string? CurrentAccountId,
    string? CurrentThreadId,
    IReadOnlyList<ControlAccountView> Accounts,
    DateTimeOffset ObservedAt);

public static class ControlLoginMethods
{
    public const string Desktop = "desktop";
    public const string Browser = "browser";
    public const string Device = "device";
    public const string AppServer = "app-server";
}

public sealed record ControlLoginStart(
    string AccountId,
    string LoginId,
    string? AuthUrl,
    DateTimeOffset StartedAt,
    string LoginMethod = ControlLoginMethods.AppServer,
    string? UserCode = null);

public sealed record ControlLoginStatus(
    string LoginId,
    string State,
    string? AccountId,
    string? Email,
    string? PlanType,
    string? Error,
    DateTimeOffset UpdatedAt);

public sealed record ControlModeChange(string Mode, string? PinnedAccountId);

public sealed record ControlMigrationStart(string JobId, string State);

public sealed record ControlMigrationStatus(
    string JobId,
    string SourceThreadId,
    string SourceAccountId,
    string TargetAccountId,
    string? TargetThreadId,
    string State,
    string? Error,
    DateTimeOffset UpdatedAt);
