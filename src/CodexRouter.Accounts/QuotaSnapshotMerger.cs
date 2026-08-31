using CodexRouter.Domain;

namespace CodexRouter.Accounts;

public sealed class QuotaSnapshotMerger
{
    public QuotaSnapshot Merge(QuotaSnapshot baseline, QuotaSparseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(update);
        if (baseline.AccountId != update.AccountId)
        {
            throw new ArgumentException("Sparse quota update belongs to a different account.", nameof(update));
        }

        var buckets = baseline.Buckets.ToList();
        foreach (var patch in update.Buckets)
        {
            var index = FindBucketIndex(buckets, patch);
            if (index >= 0)
            {
                var current = buckets[index];
                buckets[index] = new QuotaBucket(
                    current.LimitId,
                    MergeNullableReference(current.LimitName, patch.LimitName),
                    current.Slot,
                    MergeValue(current.UsedPercent, patch.UsedPercent),
                    MergeNullableValue(current.WindowDuration, patch.WindowDuration),
                    MergeNullableValue(current.ResetsAt, patch.ResetsAt));
                continue;
            }

            if (!patch.UsedPercent.IsPresent || !patch.UsedPercent.HasValue)
            {
                continue;
            }

            buckets.Add(new QuotaBucket(
                patch.LimitId,
                PatchValueOrNull(patch.LimitName),
                patch.Slot,
                patch.UsedPercent.Value,
                PatchValueOrNull(patch.WindowDuration),
                PatchValueOrNull(patch.ResetsAt)));
        }

        return new QuotaSnapshot(
            baseline.AccountId,
            buckets,
            update.ReceivedAt,
            MergeNullableReference(baseline.PlanType, update.PlanType),
            MergeNullableReference(baseline.RateLimitReachedType, update.RateLimitReachedType),
            MergeNullableValue(baseline.SpendControlReached, update.SpendControlReached),
            MergeNullableValue(baseline.HasCredits, update.HasCredits),
            MergeNullableValue(baseline.UnlimitedCredits, update.UnlimitedCredits),
            MergeNullableReference(baseline.CreditBalance, update.CreditBalance));
    }

    private static int FindBucketIndex(IReadOnlyList<QuotaBucket> buckets, QuotaBucketPatch patch)
    {
        for (var index = 0; index < buckets.Count; index++)
        {
            if (buckets[index].Slot == patch.Slot &&
                string.Equals(buckets[index].LimitId, patch.LimitId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        if (!string.Equals(patch.LimitId, "default", StringComparison.Ordinal))
        {
            return -1;
        }

        var slotMatches = buckets
            .Select((bucket, index) => (bucket, index))
            .Where(candidate => candidate.bucket.Slot == patch.Slot)
            .ToArray();
        return slotMatches.Length == 1 ? slotMatches[0].index : -1;
    }

    private static T MergeValue<T>(T current, OptionalPatch<T> patch) where T : struct =>
        patch.IsPresent && patch.HasValue ? patch.Value : current;

    private static T? MergeNullableValue<T>(T? current, OptionalPatch<T> patch) where T : struct =>
        patch.IsPresent && patch.HasValue ? patch.Value : current;

    private static string? MergeNullableReference(string? current, OptionalPatch<string> patch) =>
        patch.IsPresent && patch.HasValue ? patch.Value : current;

    private static T? PatchValueOrNull<T>(OptionalPatch<T> patch) where T : struct =>
        patch.IsPresent && patch.HasValue ? patch.Value : null;

    private static string? PatchValueOrNull(OptionalPatch<string> patch) =>
        patch.IsPresent && patch.HasValue ? patch.Value : null;
}
