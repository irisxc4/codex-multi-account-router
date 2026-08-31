using CodexRouter.Domain;

namespace CodexRouter.Accounts;

public enum UsageAvailability
{
    Available,
    Unsupported,
    Failed
}

public sealed record UsageReadResult(
    UsageAvailability Availability,
    UsageSnapshot? Snapshot,
    string? Error = null);

public sealed record QuotaState(
    QuotaSnapshot? Snapshot,
    bool IsStale,
    TimeSpan? Age,
    DateTimeOffset CheckedAt);

public sealed record LoginCompletion(
    string LoginId,
    bool Success,
    string? Error,
    DateTimeOffset CompletedAt);

public sealed record AccountOnboardingResult(
    AccountProfile Profile,
    ChatGptLoginSession LoginSession);

public sealed record AccountServiceOptions(
    TimeSpan? LoginTimeout = null,
    TimeSpan? QuotaStaleAfter = null,
    int ShortReservePercent = 15,
    int LongReservePercent = 8,
    TimeSpan? QuotaRefreshInterval = null,
    bool EnableQuotaBackgroundRefresh = true)
{
    public TimeSpan EffectiveLoginTimeout => LoginTimeout ?? TimeSpan.FromMinutes(10);
    public TimeSpan EffectiveQuotaStaleAfter => QuotaStaleAfter ?? TimeSpan.FromMinutes(5);
    public TimeSpan EffectiveQuotaRefreshInterval => QuotaRefreshInterval ?? TimeSpan.FromMinutes(2);
}

public class AccountServiceException : Exception
{
    public AccountServiceException(string message, Exception? innerException = null) : base(message, innerException) { }
}

public sealed class AccountNotFoundException : AccountServiceException
{
    public AccountNotFoundException(AccountId accountId) : base($"Account '{accountId}' was not found.") => AccountId = accountId;
    public AccountId AccountId { get; }
}

public sealed class AccountDeleteBlockedException : AccountServiceException
{
    public AccountDeleteBlockedException(AccountId accountId, int routeCount)
        : base($"Account '{accountId}' still owns {routeCount} sticky thread route(s). Migrate or remove them before deleting the profile.")
    {
        AccountId = accountId;
        RouteCount = routeCount;
    }

    public AccountId AccountId { get; }
    public int RouteCount { get; }
}
