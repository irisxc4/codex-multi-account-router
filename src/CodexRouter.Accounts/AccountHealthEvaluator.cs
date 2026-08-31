using CodexRouter.Domain;

namespace CodexRouter.Accounts;

public sealed class AccountHealthEvaluator
{
    public AccountHealth Evaluate(
        AccountProfile profile,
        AccountObservation? observation,
        QuotaSnapshot? quota,
        DateTimeOffset now,
        int shortReservePercent = 15,
        int longReservePercent = 8)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.Enabled)
        {
            return new AccountHealth(profile.Id, AccountHealthState.Disabled, now, "account disabled");
        }

        if (observation is { RequiresOpenAiAuth: true, AuthKind: AccountAuthKind.None })
        {
            return new AccountHealth(profile.Id, AccountHealthState.AuthRequired, now, "authentication required");
        }

        if (quota is null)
        {
            return new AccountHealth(profile.Id, AccountHealthState.Unknown, now, "quota not observed yet");
        }

        if (quota.Buckets.Count == 0)
        {
            return new AccountHealth(profile.Id, AccountHealthState.Unknown, now, "quota has no usable limit bucket");
        }

        if (quota.IsRateLimited)
        {
            var reset = quota.Buckets
                .Select(static bucket => bucket.ResetsAt)
                .Where(value => value is not null && value > now)
                .Min();
            return new AccountHealth(profile.Id, AccountHealthState.Cooldown, now,
                quota.RateLimitReachedType ?? "rate limit reached", reset);
        }

        // `codex` is the account-wide cap. Model-specific buckets are
        // additional request constraints and must not drain the whole account
        // merely because an unrelated model is near its reserve.
        var generalBuckets = quota.Buckets
            .Where(static bucket => bucket.LimitId.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
                                    bucket.LimitId.Equals("default", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var effectiveBuckets = generalBuckets.Length > 0 ? generalBuckets : quota.Buckets;
        foreach (var bucket in effectiveBuckets)
        {
            var reserve = bucket.WindowDuration is { } duration && duration > TimeSpan.FromDays(1)
                ? longReservePercent
                : shortReservePercent;
            if (bucket.RemainingPercent <= reserve)
            {
                return new AccountHealth(profile.Id, AccountHealthState.Draining, now,
                    $"quota reserve reached: {bucket.LimitId}/{bucket.Slot} remaining={bucket.RemainingPercent}% reserve={reserve}%");
            }
        }

        return new AccountHealth(profile.Id, AccountHealthState.Healthy, now);
    }

    public AccountHealth Degraded(AccountId accountId, string reason, DateTimeOffset? now = null) =>
        new(accountId, AccountHealthState.Degraded, now ?? DateTimeOffset.UtcNow, reason);

    public AccountHealth AuthRequired(AccountId accountId, string reason, DateTimeOffset? now = null) =>
        new(accountId, AccountHealthState.AuthRequired, now ?? DateTimeOffset.UtcNow, reason);
}
