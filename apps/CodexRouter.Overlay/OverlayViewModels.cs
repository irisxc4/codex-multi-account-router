using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CodexRouter.Control;

namespace CodexRouter.Overlay;

public sealed class OverlayViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan QuotaStaleAfter = TimeSpan.FromMinutes(5);
    private string _currentAlias = UiText.RouterName;
    private string _remainingText = "—";
    private int? _remainingPercent;
    private string _modeText = UiText.ModeAuto;
    private string _statusText = UiText.WaitingForCodex;
    private string? _currentAccountId;
    private string? _currentThreadAccountId;
    private string? _currentThreadId;
    private string? _selectedHealth;
    private bool _hasAccounts;
    private string _pillAccountName = UiText.NotSignedIn;
    private string _accountInitial = "C";
    private bool _isRoutingEnabled = true;
    private string _routingToggleText = UiText.EnableRouting;
    private string _integrationStatusText = UiText.IntegrationOff;
    private string _integrationActionText = UiText.EnableDesktopIntegration;
    private readonly Dictionary<string, string?> _quotaRefreshErrors = new(StringComparer.Ordinal);

    public ObservableCollection<AccountRowViewModel> Accounts { get; } = new();

    public string CurrentAlias { get => _currentAlias; private set => Set(ref _currentAlias, value); }
    public string RemainingText { get => _remainingText; private set => Set(ref _remainingText, value); }
    public int? RemainingPercent { get => _remainingPercent; private set => Set(ref _remainingPercent, value); }
    public string ModeText { get => _modeText; private set => Set(ref _modeText, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string? CurrentAccountId { get => _currentAccountId; private set => Set(ref _currentAccountId, value); }
    /// <summary>Account that owns the currently displayed thread, independent of the pinned UI selection.</summary>
    public string? CurrentThreadAccountId { get => _currentThreadAccountId; private set => Set(ref _currentThreadAccountId, value); }
    public string? CurrentThreadId { get => _currentThreadId; private set => Set(ref _currentThreadId, value); }
    public string? SelectedHealth { get => _selectedHealth; private set => Set(ref _selectedHealth, value); }
    public bool HasAccounts { get => _hasAccounts; private set => Set(ref _hasAccounts, value); }
    public string PillAccountName { get => _pillAccountName; private set => Set(ref _pillAccountName, value); }
    public string AccountInitial { get => _accountInitial; private set => Set(ref _accountInitial, value); }
    public bool IsRoutingEnabled { get => _isRoutingEnabled; private set => Set(ref _isRoutingEnabled, value); }
    public string RoutingToggleText { get => _routingToggleText; private set => Set(ref _routingToggleText, value); }
    public string IntegrationStatusText { get => _integrationStatusText; private set => Set(ref _integrationStatusText, value); }
    public string IntegrationActionText { get => _integrationActionText; private set => Set(ref _integrationActionText, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(ControlSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // In PIN mode the pinned account is the product's visible selection. The
        // worker projection remains available separately so an account click can
        // decide whether the current thread needs migration.
        var selected = string.Equals(snapshot.RouterMode, "Pinned", StringComparison.OrdinalIgnoreCase) &&
                       snapshot.PinnedAccountId is { } pinnedInPinnedMode
            ? snapshot.Accounts.FirstOrDefault(account => account.Id == pinnedInPinnedMode)
                ?? snapshot.Accounts.FirstOrDefault(account => account.IsCurrent)
            : snapshot.Accounts.FirstOrDefault(account => account.IsCurrent)
                ?? (snapshot.PinnedAccountId is { } pinned
                    ? snapshot.Accounts.FirstOrDefault(account => account.Id == pinned)
                    : null)
            ?? snapshot.Accounts.FirstOrDefault();

        CurrentAccountId = selected?.Id;
        CurrentThreadAccountId = snapshot.CurrentAccountId;
        CurrentThreadId = snapshot.CurrentThreadId;
        SelectedHealth = selected?.Health;
        CurrentAlias = selected?.Alias ?? UiText.RouterName;
        PillAccountName = ResolvePillAccountName(selected);
        AccountInitial = ResolveAccountInitial(PillAccountName, selected is not null);
        RemainingPercent = SelectPrimaryRemaining(selected?.QuotaBuckets);
        RemainingText = RemainingPercent is { } remaining ? $"{remaining}%" : "—";
        ModeText = snapshot.RouterMode switch
        {
            "Pinned" => UiText.ModePinned,
            "Off" => UiText.ModeOff,
            _ => UiText.ModeAuto
        };
        IsRoutingEnabled = !string.Equals(snapshot.RouterMode, "Off", StringComparison.OrdinalIgnoreCase);
        RoutingToggleText = IsRoutingEnabled ? UiText.DisableRouting : UiText.EnableRouting;
        HasAccounts = snapshot.Accounts.Count > 0;
        StatusText = snapshot.Accounts.Count == 0
            ? UiText.NoAccountsConfigured
            : selected is null
                ? UiText.NoActiveAccount
                : $"{UiText.LocalizeHealth(selected.Health)} · {selected.PlanType ?? "Codex"}";

        var existing = Accounts.ToDictionary(static account => account.Id, StringComparer.Ordinal);
        foreach (var account in snapshot.Accounts)
        {
            if (!existing.TryGetValue(account.Id, out var row))
            {
                row = new AccountRowViewModel(account.Id);
                Accounts.Add(row);
            }
            row.Apply(
                account,
                snapshot.PinnedAccountId == account.Id,
                _quotaRefreshErrors.TryGetValue(account.Id, out var refreshError) ? refreshError : null);
        }
        for (var index = Accounts.Count - 1; index >= 0; index--)
        {
            if (!snapshot.Accounts.Any(account => account.Id == Accounts[index].Id))
            {
                Accounts.RemoveAt(index);
            }
        }
    }

    public void SetIntegrationPresentation(string statusText, string actionText)
    {
        IntegrationStatusText = string.IsNullOrWhiteSpace(statusText) ? UiText.IntegrationOff : statusText;
        IntegrationActionText = string.IsNullOrWhiteSpace(actionText) ? UiText.EnableDesktopIntegration : actionText;
    }

    public void MarkQuotaRefreshStarted(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;
        _quotaRefreshErrors[accountId] = UiText.QuotaSyncInProgress;
        Accounts.FirstOrDefault(account => string.Equals(account.Id, accountId, StringComparison.Ordinal))
            ?.SetQuotaRefreshError(UiText.QuotaSyncInProgress);
    }

    public void MarkQuotaRefreshSucceeded(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;
        _quotaRefreshErrors.Remove(accountId);
        Accounts.FirstOrDefault(account => string.Equals(account.Id, accountId, StringComparison.Ordinal))
            ?.SetQuotaRefreshError(null);
    }

    public void MarkQuotaRefreshFailed(string accountId, string? error)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;
        var message = string.IsNullOrWhiteSpace(error) ? UiText.QuotaSyncFailed : error.Trim();
        _quotaRefreshErrors[accountId] = message;
        Accounts.FirstOrDefault(account => string.Equals(account.Id, accountId, StringComparison.Ordinal))
            ?.SetQuotaRefreshError(message);
    }

    private static int? SelectPrimaryRemaining(IReadOnlyList<ControlQuotaBucket>? buckets)
    {
        var usable = buckets?.Where(IsUsableBucket).ToArray() ?? Array.Empty<ControlQuotaBucket>();
        if (usable.Length == 0) return null;

        // The official app-server treats the general "codex" bucket as the primary
        // summary. Model-specific buckets are additional views and must not replace it.
        var general = usable.Where(IsGeneralCodexBucket).ToArray();
        var source = general.Length > 0 ? general : usable;
        return source.Min(static bucket => bucket.RemainingPercent);
    }

    internal static bool IsGeneralCodexBucket(ControlQuotaBucket bucket) =>
        string.Equals(bucket.LimitId, "codex", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(bucket.LimitName, "codex", StringComparison.OrdinalIgnoreCase);

    internal static bool IsUsableBucket(ControlQuotaBucket bucket) =>
        bucket.RemainingPercent is >= 0 and <= 100 &&
        bucket.UsedPercent is >= 0 and <= 100;

    internal static TimeSpan QuotaStaleThreshold => QuotaStaleAfter;

    private static string ResolvePillAccountName(ControlAccountView? account)
    {
        if (account is null) return UiText.NotSignedIn;
        if (!string.IsNullOrWhiteSpace(account.Alias)) return account.Alias.Trim();
        if (!string.IsNullOrWhiteSpace(account.Email)) return account.Email.Trim();
        return "Codex";
    }

    private static string ResolveAccountInitial(string accountName, bool hasAccount)
    {
        if (!hasAccount || string.IsNullOrWhiteSpace(accountName)) return "C";
        return StringInfo.GetNextTextElement(accountName.Trim()).ToUpperInvariant();
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum AccountSwitchAction
{
    PinOnly,
    MigrateThenPin
}

public static class AccountSwitchPolicy
{
    public static AccountSwitchAction Decide(
        string? currentThreadId,
        string? currentThreadAccountId,
        string targetAccountId)
    {
        if (string.IsNullOrWhiteSpace(targetAccountId))
        {
            throw new ArgumentException("Target account ID cannot be empty.", nameof(targetAccountId));
        }
        return !string.IsNullOrWhiteSpace(currentThreadId) &&
               !string.IsNullOrWhiteSpace(currentThreadAccountId) &&
               !string.Equals(currentThreadAccountId, targetAccountId, StringComparison.Ordinal)
            ? AccountSwitchAction.MigrateThenPin
            : AccountSwitchAction.PinOnly;
    }
}

public static class ThreadDeepLink
{
    public static Uri Create(string targetThreadId)
    {
        if (string.IsNullOrWhiteSpace(targetThreadId))
        {
            throw new ArgumentException("Target thread ID cannot be empty.", nameof(targetThreadId));
        }
        return new Uri($"codex://threads/{Uri.EscapeDataString(targetThreadId)}", UriKind.Absolute);
    }
}

public sealed class AccountRowViewModel : INotifyPropertyChanged
{
    private string _alias = string.Empty;
    private string _detail = string.Empty;
    private string _shortQuota = "—";
    private string _longQuota = "—";
    private string _specialQuota = "—";
    private string _quotaSyncText = UiText.QuotaNeverSynced;
    private string? _quotaErrorText;
    private DateTimeOffset? _quotaFetchedAt;
    private int _shortUsed;
    private int _longUsed;
    private bool _hasShortQuota;
    private bool _hasLongQuota;
    private bool _hasSpecialQuota;
    private string _health = UiText.LocalizeHealth("Unknown");
    private string _rawHealth = "Unknown";
    private bool _isCurrent;
    private bool _isPinned;
    private bool _enabled;

    public AccountRowViewModel(string id) => Id = id;

    public string Id { get; }
    public string Alias { get => _alias; private set => Set(ref _alias, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public string ShortQuota { get => _shortQuota; private set => Set(ref _shortQuota, value); }
    public string LongQuota { get => _longQuota; private set => Set(ref _longQuota, value); }
    public string SpecialQuota { get => _specialQuota; private set => Set(ref _specialQuota, value); }
    public string QuotaSyncText { get => _quotaSyncText; private set => Set(ref _quotaSyncText, value); }
    public string? QuotaErrorText { get => _quotaErrorText; private set => Set(ref _quotaErrorText, value); }
    public DateTimeOffset? QuotaFetchedAt { get => _quotaFetchedAt; private set => Set(ref _quotaFetchedAt, value); }
    public bool NeedsQuotaRefresh => QuotaFetchedAt is not { } fetchedAt ||
        DateTimeOffset.UtcNow - fetchedAt > OverlayViewModel.QuotaStaleThreshold;
    public int ShortUsed { get => _shortUsed; private set => Set(ref _shortUsed, value); }
    public int LongUsed { get => _longUsed; private set => Set(ref _longUsed, value); }
    public bool HasShortQuota { get => _hasShortQuota; private set => Set(ref _hasShortQuota, value); }
    public bool HasLongQuota { get => _hasLongQuota; private set => Set(ref _hasLongQuota, value); }
    public bool HasSpecialQuota { get => _hasSpecialQuota; private set => Set(ref _hasSpecialQuota, value); }
    public string Health { get => _health; private set => Set(ref _health, value); }
    public string RawHealth { get => _rawHealth; private set => Set(ref _rawHealth, value); }
    public bool IsCurrent { get => _isCurrent; private set => Set(ref _isCurrent, value); }
    public bool IsPinned { get => _isPinned; private set => Set(ref _isPinned, value); }
    public bool Enabled { get => _enabled; private set => Set(ref _enabled, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(ControlAccountView account, bool isPinned, string? quotaRefreshError = null)
    {
        Alias = account.Alias;
        Detail = string.Join(" · ", new[] { account.Email, account.PlanType }
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!));
        RawHealth = account.Health;
        Health = UiText.LocalizeHealth(account.Health);
        IsCurrent = account.IsCurrent;
        IsPinned = isPinned;
        Enabled = account.Enabled;

        var usable = account.QuotaBuckets.Where(OverlayViewModel.IsUsableBucket).ToArray();
        var general = usable.Where(OverlayViewModel.IsGeneralCodexBucket).ToArray();
        var primary = general.Length > 0 ? general : usable;
        var shortBucket = SelectWindowBucket(primary, shortWindow: true);
        var longBucket = SelectWindowBucket(primary, shortWindow: false);

        HasShortQuota = shortBucket is not null;
        HasLongQuota = longBucket is not null;
        ShortUsed = shortBucket?.UsedPercent ?? 0;
        LongUsed = longBucket?.UsedPercent ?? 0;
        ShortQuota = BucketLabel(shortBucket);
        LongQuota = BucketLabel(longBucket);
        SpecialQuota = BuildSpecialQuota(usable);
        HasSpecialQuota = !string.Equals(SpecialQuota, "—", StringComparison.Ordinal);
        QuotaFetchedAt = account.QuotaFetchedAt;
        QuotaErrorText = quotaRefreshError;
        QuotaSyncText = BuildQuotaSyncText(account.QuotaFetchedAt, quotaRefreshError);
    }

    public void SetQuotaRefreshError(string? error)
    {
        QuotaErrorText = error;
        QuotaSyncText = string.IsNullOrWhiteSpace(error)
            ? QuotaSyncText
            : BuildQuotaSyncText(null, error);
    }

    private static ControlQuotaBucket? SelectWindowBucket(
        IReadOnlyList<ControlQuotaBucket> buckets,
        bool shortWindow)
    {
        var candidates = buckets
            .Where(bucket => bucket.WindowMinutes is not null &&
                (shortWindow ? bucket.WindowMinutes <= 1440 : bucket.WindowMinutes > 1440))
            .OrderBy(static bucket => bucket.RemainingPercent)
            .ThenBy(static bucket => bucket.WindowMinutes)
            .ToArray();
        return candidates.FirstOrDefault();
    }

    private static string BuildSpecialQuota(IReadOnlyList<ControlQuotaBucket> buckets)
    {
        var groups = buckets
            .Where(static bucket => !OverlayViewModel.IsGeneralCodexBucket(bucket))
            .GroupBy(static bucket => string.IsNullOrWhiteSpace(bucket.LimitName)
                ? (string.IsNullOrWhiteSpace(bucket.LimitId) ? UiText.UnknownQuotaLimit : bucket.LimitId)
                : bucket.LimitName!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
                $"{group.Key}: {string.Join(" / ", group.OrderBy(static bucket => bucket.WindowMinutes).Select(BucketLabel))}")
            .ToArray();
        return groups.Length == 0 ? "—" : string.Join(" · ", groups);
    }

    private static string BuildQuotaSyncText(DateTimeOffset? fetchedAt, string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return string.Equals(error, UiText.QuotaSyncInProgress, StringComparison.Ordinal)
                ? error
                : UiText.QuotaSyncFailed;
        }
        if (fetchedAt is not { } timestamp) return UiText.QuotaNeverSynced;
        var local = timestamp.ToLocalTime();
        var age = DateTimeOffset.UtcNow - timestamp;
        return age > OverlayViewModel.QuotaStaleThreshold
            ? UiText.QuotaStaleAt(local.ToString("HH:mm", CultureInfo.CurrentCulture))
            : UiText.QuotaSyncedAt(local.ToString("HH:mm", CultureInfo.CurrentCulture));
    }

    private static string BucketLabel(ControlQuotaBucket? bucket)
    {
        if (bucket is null) return "—";
        var minutes = bucket.WindowMinutes;
        var duration = minutes is > 0 and <= 1440
            ? $"{minutes.Value / 60:0.#}h"
            : minutes is > 1440
                ? $"{minutes.Value / 1440:0.#}d"
                : bucket.LimitName ?? bucket.LimitId;
        var remaining = OverlayViewModel.IsUsableBucket(bucket) ? $"{bucket.RemainingPercent}%" : "—";
        return $"{duration}  {remaining}";
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
