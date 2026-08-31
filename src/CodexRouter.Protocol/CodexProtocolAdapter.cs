using System.Text.Json;
using CodexRouter.Domain;

namespace CodexRouter.Protocol;

public sealed record ProtocolMapResult<T>(T? Value, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0 && Value is not null;
    public static ProtocolMapResult<T> Success(T value) => new(value, Array.Empty<string>());
    public static ProtocolMapResult<T> Failure(params string[] errors) => new(default, errors);
}

public sealed class CodexProtocolAdapter
{
    public ProtocolMapResult<AccountObservation> MapAccountRead(AccountId accountId, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var payload = Unwrap(document.RootElement);
            if (payload.ValueKind != JsonValueKind.Object)
                return ProtocolMapResult<AccountObservation>.Failure("account/read payload is not an object.");

            var requiresOpenAiAuth = ReadBoolean(payload, "requiresOpenaiAuth") ?? false;
            AccountAuthKind authKind = AccountAuthKind.None;
            string? email = null;
            string? planType = null;

            if (TryObject(payload, "account", out var account))
            {
                var type = ReadString(account, "type");
                authKind = type switch
                {
                    "chatgpt" => AccountAuthKind.ChatGpt,
                    "apiKey" => AccountAuthKind.ApiKey,
                    "amazonBedrock" => AccountAuthKind.AmazonBedrock,
                    "personalAccessToken" => AccountAuthKind.PersonalAccessToken,
                    null => AccountAuthKind.Unknown,
                    _ => AccountAuthKind.Unknown
                };
                email = ReadString(account, "email");
                planType = ReadString(account, "planType");
            }

            return ProtocolMapResult<AccountObservation>.Success(new AccountObservation(
                accountId,
                authKind,
                email,
                planType,
                requiresOpenAiAuth,
                DateTimeOffset.UtcNow));
        }
        catch (JsonException ex)
        {
            return ProtocolMapResult<AccountObservation>.Failure($"Invalid account/read JSON: {ex.Message}");
        }
    }

    public ProtocolMapResult<QuotaSnapshot> MapRateLimitsRead(AccountId accountId, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var payload = Unwrap(document.RootElement);
            if (payload.ValueKind != JsonValueKind.Object)
                return ProtocolMapResult<QuotaSnapshot>.Failure("account/rateLimits/read payload is not an object.");

            var buckets = new List<QuotaBucket>();
            string? planType = null;
            string? reachedType = null;
            bool? spendControlReached = null;
            bool? hasCredits = null;
            bool? unlimitedCredits = null;
            string? creditBalance = null;

            var usedMultiView = payload.TryGetProperty("rateLimitsByLimitId", out var byLimit) &&
                                byLimit.ValueKind == JsonValueKind.Object &&
                                byLimit.EnumerateObject().Any();

            if (usedMultiView)
            {
                foreach (var entry in byLimit.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                    var snapshot = entry.Value;
                    var limitId = ReadString(snapshot, "limitId") ?? entry.Name;
                    var limitName = ReadString(snapshot, "limitName");
                    AddWindow(buckets, snapshot, limitId, limitName, "primary", QuotaBucketSlot.Primary);
                    AddWindow(buckets, snapshot, limitId, limitName, "secondary", QuotaBucketSlot.Secondary);

                    planType ??= ReadString(snapshot, "planType");
                    reachedType ??= ReadString(snapshot, "rateLimitReachedType");
                    MergeBoolean(ref spendControlReached, ReadBoolean(snapshot, "spendControlReached"));
                    ReadCredits(snapshot, ref hasCredits, ref unlimitedCredits, ref creditBalance);
                }
            }

            // Current app-server builds expose both a backward-compatible
            // primary view and a multi-limit map. The map normally contains
            // the global `codex` entry, but merge a missing legacy entry so a
            // future/partial response cannot drop the account-wide cap. The
            // multi-limit value wins when both describe the same slot.
            if (TryObject(payload, "rateLimits", out var legacy))
            {
                // The backward-compatible view omits limitId in some app-server
                // versions; when a multi-view is present it represents the
                // server-selected global Codex bucket.
                var limitId = ReadString(legacy, "limitId") ?? (usedMultiView ? "codex" : "default");
                var limitName = ReadString(legacy, "limitName");
                var legacyBuckets = new List<QuotaBucket>();
                AddWindow(legacyBuckets, legacy, limitId, limitName, "primary", QuotaBucketSlot.Primary);
                AddWindow(legacyBuckets, legacy, limitId, limitName, "secondary", QuotaBucketSlot.Secondary);
                foreach (var bucket in legacyBuckets)
                {
                    if (!buckets.Any(existing =>
                            string.Equals(existing.LimitId, bucket.LimitId, StringComparison.OrdinalIgnoreCase) &&
                            existing.Slot == bucket.Slot))
                    {
                        buckets.Add(bucket);
                    }
                }
                planType ??= ReadString(legacy, "planType");
                reachedType ??= ReadString(legacy, "rateLimitReachedType");
                MergeBoolean(ref spendControlReached, ReadBoolean(legacy, "spendControlReached"));
                ReadCredits(legacy, ref hasCredits, ref unlimitedCredits, ref creditBalance);
            }

            var snapshotResult = new QuotaSnapshot(
                accountId,
                buckets,
                DateTimeOffset.UtcNow,
                planType,
                reachedType,
                spendControlReached,
                hasCredits,
                unlimitedCredits,
                creditBalance);
            return ProtocolMapResult<QuotaSnapshot>.Success(snapshotResult);
        }
        catch (JsonException ex)
        {
            return ProtocolMapResult<QuotaSnapshot>.Failure($"Invalid account/rateLimits/read JSON: {ex.Message}");
        }
    }

    public ProtocolMapResult<QuotaSparseUpdate> MapRateLimitsUpdated(AccountId accountId, string json, DateTimeOffset? receivedAt = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var payload = Unwrap(document.RootElement);
            if (payload.ValueKind != JsonValueKind.Object)
                return ProtocolMapResult<QuotaSparseUpdate>.Failure("account/rateLimits/updated payload is not an object.");

            var patches = new List<QuotaBucketPatch>();
            var planType = OptionalPatch<string>.Missing;
            var reachedType = OptionalPatch<string>.Missing;
            var spendControl = OptionalPatch<bool>.Missing;
            var hasCredits = OptionalPatch<bool>.Missing;
            var unlimitedCredits = OptionalPatch<bool>.Missing;
            var balance = OptionalPatch<string>.Missing;

            var usedMultiView = payload.TryGetProperty("rateLimitsByLimitId", out var byLimit) &&
                                byLimit.ValueKind == JsonValueKind.Object &&
                                byLimit.EnumerateObject().Any();
            if (usedMultiView)
            {
                foreach (var entry in byLimit.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                    CollectSnapshotPatch(entry.Value, entry.Name, patches,
                        ref planType, ref reachedType, ref spendControl,
                        ref hasCredits, ref unlimitedCredits, ref balance);
                }
            }

            if (TryObject(payload, "rateLimits", out var rateLimits))
            {
                var legacyPatches = new List<QuotaBucketPatch>();
                var legacyPlanType = OptionalPatch<string>.Missing;
                var legacyReachedType = OptionalPatch<string>.Missing;
                var legacySpendControl = OptionalPatch<bool>.Missing;
                var legacyHasCredits = OptionalPatch<bool>.Missing;
                var legacyUnlimitedCredits = OptionalPatch<bool>.Missing;
                var legacyBalance = OptionalPatch<string>.Missing;
                CollectSnapshotPatch(rateLimits, ReadString(rateLimits, "limitId") ?? (usedMultiView ? "codex" : "default"), legacyPatches,
                    ref legacyPlanType, ref legacyReachedType, ref legacySpendControl,
                    ref legacyHasCredits, ref legacyUnlimitedCredits, ref legacyBalance);
                foreach (var patch in legacyPatches)
                {
                    if (!patches.Any(existing =>
                            string.Equals(existing.LimitId, patch.LimitId, StringComparison.OrdinalIgnoreCase) &&
                            existing.Slot == patch.Slot))
                    {
                        patches.Add(patch);
                    }
                }
                MergeFallbackPatch(ref planType, legacyPlanType);
                MergeFallbackPatch(ref reachedType, legacyReachedType);
                MergeFallbackPatch(ref spendControl, legacySpendControl);
                MergeFallbackPatch(ref hasCredits, legacyHasCredits);
                MergeFallbackPatch(ref unlimitedCredits, legacyUnlimitedCredits);
                MergeFallbackPatch(ref balance, legacyBalance);
            }

            return ProtocolMapResult<QuotaSparseUpdate>.Success(new QuotaSparseUpdate(
                accountId,
                patches,
                planType,
                reachedType,
                spendControl,
                hasCredits,
                unlimitedCredits,
                balance,
                receivedAt ?? DateTimeOffset.UtcNow));
        }
        catch (JsonException ex)
        {
            return ProtocolMapResult<QuotaSparseUpdate>.Failure($"Invalid account/rateLimits/updated JSON: {ex.Message}");
        }
    }

    public ProtocolMapResult<UsageSnapshot> MapUsageRead(AccountId accountId, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var payload = Unwrap(document.RootElement);
            if (payload.ValueKind != JsonValueKind.Object)
                return ProtocolMapResult<UsageSnapshot>.Failure("account/usage/read payload is not an object.");

            long? lifetimeTokens = null;
            long? peakDailyTokens = null;
            long? longestRunningTurnSeconds = null;
            long? currentStreakDays = null;
            long? longestStreakDays = null;
            if (TryObject(payload, "summary", out var summary))
            {
                lifetimeTokens = ReadInt64(summary, "lifetimeTokens");
                peakDailyTokens = ReadInt64(summary, "peakDailyTokens");
                longestRunningTurnSeconds = ReadInt64(summary, "longestRunningTurnSec");
                currentStreakDays = ReadInt64(summary, "currentStreakDays");
                longestStreakDays = ReadInt64(summary, "longestStreakDays");
            }

            var dailyBuckets = new List<UsageDailyBucket>();
            if (payload.TryGetProperty("dailyUsageBuckets", out var daily) && daily.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in daily.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) continue;
                    var startDate = ReadString(row, "startDate");
                    var tokens = ReadInt64(row, "tokens");
                    if (tokens.HasValue && DateOnly.TryParse(startDate, out var parsedDate))
                        dailyBuckets.Add(new UsageDailyBucket(parsedDate, tokens.Value));
                }
            }

            return ProtocolMapResult<UsageSnapshot>.Success(new UsageSnapshot(
                accountId,
                DateTimeOffset.UtcNow,
                lifetimeTokens,
                peakDailyTokens,
                longestRunningTurnSeconds,
                currentStreakDays,
                longestStreakDays,
                dailyBuckets));
        }
        catch (JsonException ex)
        {
            return ProtocolMapResult<UsageSnapshot>.Failure($"Invalid account/usage/read JSON: {ex.Message}");
        }
    }

    private static JsonElement Unwrap(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return root;
        if (root.TryGetProperty("result", out var result) && result.ValueKind != JsonValueKind.Null) return result;
        if (root.TryGetProperty("params", out var parameters) && parameters.ValueKind != JsonValueKind.Null) return parameters;
        return root;
    }

    private static void CollectSnapshotPatch(
        JsonElement snapshot,
        string fallbackLimitId,
        ICollection<QuotaBucketPatch> patches,
        ref OptionalPatch<string> planType,
        ref OptionalPatch<string> reachedType,
        ref OptionalPatch<bool> spendControl,
        ref OptionalPatch<bool> hasCredits,
        ref OptionalPatch<bool> unlimitedCredits,
        ref OptionalPatch<string> balance)
    {
        var limitId = ReadString(snapshot, "limitId") ?? fallbackLimitId;
        AddWindowPatch(patches, snapshot, limitId, "primary", QuotaBucketSlot.Primary);
        AddWindowPatch(patches, snapshot, limitId, "secondary", QuotaBucketSlot.Secondary);

        MergePatch(ref planType, ReadStringPatch(snapshot, "planType"));
        MergePatch(ref reachedType, ReadStringPatch(snapshot, "rateLimitReachedType"));
        MergePatch(ref spendControl, ReadBooleanPatch(snapshot, "spendControlReached"));

        if (snapshot.TryGetProperty("credits", out var credits))
        {
            if (credits.ValueKind == JsonValueKind.Null)
            {
                MergePatch(ref hasCredits, OptionalPatch<bool>.Null);
                MergePatch(ref unlimitedCredits, OptionalPatch<bool>.Null);
                MergePatch(ref balance, OptionalPatch<string>.Null);
            }
            else if (credits.ValueKind == JsonValueKind.Object)
            {
                MergePatch(ref hasCredits, ReadBooleanPatch(credits, "hasCredits"));
                MergePatch(ref unlimitedCredits, ReadBooleanPatch(credits, "unlimited"));
                MergePatch(ref balance, ReadStringPatch(credits, "balance"));
            }
        }
    }

    private static void AddWindow(List<QuotaBucket> buckets, JsonElement snapshot, string limitId, string? limitName, string propertyName, QuotaBucketSlot slot)
    {
        if (!TryObject(snapshot, propertyName, out var window)) return;
        var usedPercent = ReadInt32(window, "usedPercent");
        if (!usedPercent.HasValue) return;
        var durationMinutes = ReadInt64(window, "windowDurationMins");
        var resetUnixSeconds = ReadInt64(window, "resetsAt");
        buckets.Add(new QuotaBucket(
            limitId,
            limitName,
            slot,
            usedPercent.Value,
            durationMinutes.HasValue ? TimeSpan.FromMinutes(durationMinutes.Value) : null,
            ToDateTimeOffset(resetUnixSeconds)));
    }

    private static void AddWindowPatch(ICollection<QuotaBucketPatch> patches, JsonElement snapshot, string limitId, string propertyName, QuotaBucketSlot slot)
    {
        if (!snapshot.TryGetProperty(propertyName, out var window)) return;
        if (window.ValueKind == JsonValueKind.Null) return;
        if (window.ValueKind != JsonValueKind.Object) return;

        var limitName = ReadStringPatch(snapshot, "limitName");
        var usedPercent = ReadInt32Patch(window, "usedPercent");
        var duration = ReadDurationPatch(window, "windowDurationMins");
        var resetsAt = ReadTimestampPatch(window, "resetsAt");
        patches.Add(new QuotaBucketPatch(limitId, slot, limitName, usedPercent, duration, resetsAt));
    }

    private static void ReadCredits(JsonElement snapshot, ref bool? hasCredits, ref bool? unlimitedCredits, ref string? balance)
    {
        if (!TryObject(snapshot, "credits", out var credits)) return;
        hasCredits ??= ReadBoolean(credits, "hasCredits");
        unlimitedCredits ??= ReadBoolean(credits, "unlimited");
        balance ??= ReadString(credits, "balance");
    }

    private static void MergeBoolean(ref bool? target, bool? value)
    {
        if (!value.HasValue) return;
        target = target.HasValue ? target.Value || value.Value : value.Value;
    }

    private static void MergePatch<T>(ref OptionalPatch<T> target, OptionalPatch<T> value)
    {
        if (!value.IsPresent) return;
        if (!target.IsPresent || value.HasValue) target = value;
    }

    private static void MergeFallbackPatch<T>(ref OptionalPatch<T> target, OptionalPatch<T> fallback)
    {
        if (!target.IsPresent && fallback.IsPresent) target = fallback;
    }

    private static OptionalPatch<string> ReadStringPatch(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return OptionalPatch<string>.Missing;
        if (value.ValueKind == JsonValueKind.Null) return OptionalPatch<string>.Null;
        return value.ValueKind == JsonValueKind.String
            ? OptionalPatch<string>.Present(value.GetString() ?? string.Empty)
            : OptionalPatch<string>.Missing;
    }

    private static OptionalPatch<bool> ReadBooleanPatch(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return OptionalPatch<bool>.Missing;
        if (value.ValueKind == JsonValueKind.Null) return OptionalPatch<bool>.Null;
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? OptionalPatch<bool>.Present(value.GetBoolean())
            : OptionalPatch<bool>.Missing;
    }

    private static OptionalPatch<int> ReadInt32Patch(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return OptionalPatch<int>.Missing;
        if (value.ValueKind == JsonValueKind.Null) return OptionalPatch<int>.Null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? OptionalPatch<int>.Present(Math.Clamp(parsed, 0, 100))
            : OptionalPatch<int>.Missing;
    }

    private static OptionalPatch<TimeSpan> ReadDurationPatch(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return OptionalPatch<TimeSpan>.Missing;
        if (value.ValueKind == JsonValueKind.Null) return OptionalPatch<TimeSpan>.Null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed)
            ? OptionalPatch<TimeSpan>.Present(TimeSpan.FromMinutes(parsed))
            : OptionalPatch<TimeSpan>.Missing;
    }

    private static OptionalPatch<DateTimeOffset> ReadTimestampPatch(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return OptionalPatch<DateTimeOffset>.Missing;
        if (value.ValueKind == JsonValueKind.Null) return OptionalPatch<DateTimeOffset>.Null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var parsed)) return OptionalPatch<DateTimeOffset>.Missing;
        var timestamp = ToDateTimeOffset(parsed);
        return timestamp.HasValue ? OptionalPatch<DateTimeOffset>.Present(timestamp.Value) : OptionalPatch<DateTimeOffset>.Missing;
    }

    private static DateTimeOffset? ToDateTimeOffset(long? unixSeconds)
    {
        if (!unixSeconds.HasValue) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static bool TryObject(JsonElement element, string property, out JsonElement result)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out result) && result.ValueKind == JsonValueKind.Object)
            return true;
        result = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadBoolean(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static int? ReadInt32(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static long? ReadInt64(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed)
            ? parsed
            : null;
}
