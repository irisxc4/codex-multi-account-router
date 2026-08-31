using System.Globalization;
using CodexRouter.Control;
using CodexRouter.Overlay;
using Xunit;

namespace CodexRouter.Overlay.Tests;

public sealed class OverlayLogicTests
{
    [Fact]
    public void Empty_snapshot_exposes_first_run_state_without_fake_account_or_quota()
    {
        var viewModel = new OverlayViewModel();
        var snapshot = new ControlSnapshot(
            "Auto",
            null,
            null,
            null,
            Array.Empty<ControlAccountView>(),
            DateTimeOffset.UtcNow);

        viewModel.Apply(snapshot);

        Assert.False(viewModel.HasAccounts);
        Assert.Empty(viewModel.Accounts);
        Assert.Equal(UiText.RouterName, viewModel.CurrentAlias);
        Assert.Equal("—", viewModel.RemainingText);
        Assert.Equal(UiText.NoAccountsConfigured, viewModel.StatusText);
        Assert.Null(viewModel.CurrentAccountId);
    }

    [Theory]
    [InlineData("zh-CN", UiLanguage.SimplifiedChinese)]
    [InlineData("zh-SG", UiLanguage.SimplifiedChinese)]
    [InlineData("zh-TW", UiLanguage.TraditionalChinese)]
    [InlineData("zh-Hant-HK", UiLanguage.TraditionalChinese)]
    [InlineData("en-US", UiLanguage.English)]
    [InlineData("ja-JP", UiLanguage.English)]
    public void Ui_language_follows_windows_ui_culture_with_english_fallback(string cultureName, UiLanguage expected)
    {
        Assert.Equal(expected, UiText.ResolveLanguage(CultureInfo.GetCultureInfo(cultureName)));
    }

    [Theory]
    [InlineData(96u, 100, 200, 1100, 900, 320, 205)]
    [InlineData(144u, 150, 300, 1650, 1350, 320, 205)]
    public void Placement_uses_target_window_dpi_and_keeps_same_visual_insets(
        uint dpi,
        int left,
        int top,
        int right,
        int bottom,
        double expectedLeftDip,
        double expectedTopDip)
    {
        var target = new CodexWindowTarget(
            new IntPtr(1),
            123,
            new NativeWindowRect(left, top, right, bottom),
            dpi,
            false,
            true);

        var placement = OverlayPlacementCalculator.Calculate(target, 164, 34);

        Assert.Equal(expectedLeftDip, placement.Left, 6);
        Assert.Equal(expectedTopDip, placement.Top, 6);
        Assert.Equal(dpi / 96.0, placement.DpiScale, 6);
    }

    [Fact]
    public void Physical_placement_uses_same_pixel_coordinate_space_as_codex_and_never_escapes_target()
    {
        var target = new CodexWindowTarget(
            new IntPtr(1),
            123,
            new NativeWindowRect(-2560, -120, -640, 1320),
            144,
            false,
            true);

        var defaultPlacement = OverlayPlacementCalculator.CalculatePhysical(target, 164, 34);
        var leftEscape = OverlayPlacementCalculator.ClampPhysical(target, 246, 51, -9999, 100);
        var rightEscape = OverlayPlacementCalculator.ClampPhysical(target, 246, 51, 9999, 9999);

        Assert.Equal(-2230, defaultPlacement.Left);
        Assert.Equal(-112, defaultPlacement.Top);
        Assert.True(leftEscape.Left >= target.Rect.Left + 6);
        Assert.True(leftEscape.Top >= target.Rect.Top + 6);
        Assert.True(rightEscape.Left + rightEscape.Width <= target.Rect.Right - 6);
        Assert.True(rightEscape.Top + rightEscape.Height <= target.Rect.Bottom - 6);
    }

    [Fact]
    public void Saved_relative_position_follows_window_and_is_clamped_inside_target()
    {
        var target = new CodexWindowTarget(
            new IntPtr(1),
            123,
            new NativeWindowRect(150, 300, 1650, 1350),
            144,
            false,
            true);

        var placement = OverlayPlacementCalculator.CalculateRelative(target, 164, 34, 450, 20);
        var clamped = OverlayPlacementCalculator.CalculateRelative(target, 164, 34, 5000, -100);

        Assert.Equal(550, placement.Left, 6);
        Assert.Equal(220, placement.Top, 6);
        Assert.Equal(932, clamped.Left, 6);
        Assert.Equal(204, clamped.Top, 6);
    }

    [Fact]
    public void Window_tracker_tolerates_transient_missing_target_before_hiding()
    {
        var target = new CodexWindowTarget(
            new IntPtr(1),
            123,
            new NativeWindowRect(0, 0, 1200, 800),
            96,
            false,
            true);
        using var tracker = new CodexWindowTracker(
            new SequenceLocator(target, null, null, null),
            missingPollTolerance: 2);

        tracker.Poll();
        Assert.Equal(target, tracker.Current);
        tracker.Poll();
        Assert.Equal(target, tracker.Current);
        tracker.Poll();
        Assert.Equal(target, tracker.Current);
        tracker.Poll();
        Assert.Null(tracker.Current);
    }

    [Fact]
    public void Popover_is_clamped_into_monitor_work_area_and_flips_above_when_bottom_is_tight()
    {
        var target = new CodexWindowTarget(
            new IntPtr(1),
            123,
            new NativeWindowRect(0, 0, 1920, 1080),
            96,
            false,
            true);
        var workArea = new NativeMonitorWorkArea(0, 0, 1920, 1040);

        var placement = OverlayPlacementCalculator.CalculatePopover(
            target,
            pillLeftDip: 1800,
            pillTopDip: 990,
            pillWidthDip: 164,
            pillHeightDip: 34,
            popoverWidthDip: 372,
            popoverHeightDip: 520,
            workArea);

        Assert.Equal(1540, placement.Left, 6);
        Assert.Equal(462, placement.Top, 6);
        Assert.True(placement.Left + 372 <= 1912);
        Assert.True(placement.Top + 520 <= 1032);
    }

    [Fact]
    public void View_model_uses_real_current_projection_and_tightest_remaining_quota()
    {
        var viewModel = new OverlayViewModel();
        var snapshot = new ControlSnapshot(
            "Auto",
            null,
            "b",
            "thread-current",
            new[]
            {
                Account("a", "A", current: false, shortRemaining: 80, longRemaining: 70),
                Account("b", "B", current: true, shortRemaining: 42, longRemaining: 71)
            },
            DateTimeOffset.UtcNow);

        viewModel.Apply(snapshot);

        Assert.Equal("B", viewModel.CurrentAlias);
        Assert.Equal("b", viewModel.CurrentAccountId);
        Assert.Equal("thread-current", viewModel.CurrentThreadId);
        Assert.Equal(42, viewModel.RemainingPercent);
        Assert.Equal("42%", viewModel.RemainingText);
        Assert.Equal(UiText.ModeAuto, viewModel.ModeText);
        Assert.Equal(2, viewModel.Accounts.Count);
    }

    [Fact]
    public void View_model_falls_back_to_pinned_then_first_account_when_no_projection_exists()
    {
        var viewModel = new OverlayViewModel();
        var pinned = new ControlSnapshot(
            "Pinned",
            "b",
            null,
            "thread-pinned",
            new[]
            {
                Account("a", "A", false, 90, 90),
                Account("b", "B", false, 60, 60)
            },
            DateTimeOffset.UtcNow);

        viewModel.Apply(pinned);
        Assert.Equal("B", viewModel.CurrentAlias);
        Assert.Equal(UiText.ModePinned, viewModel.ModeText);
        Assert.True(viewModel.Accounts.Single(account => account.Id == "b").IsPinned);

        viewModel.Apply(pinned with { RouterMode = "Off", PinnedAccountId = null });
        Assert.Equal("A", viewModel.CurrentAlias);
        Assert.Equal(UiText.ModeOff, viewModel.ModeText);
    }

    [Fact]
    public void View_model_prefers_pinned_account_for_the_visible_selection_in_pinned_mode()
    {
        var viewModel = new OverlayViewModel();
        viewModel.Apply(new ControlSnapshot(
            "Pinned",
            "b",
            "a",
            "thread-a",
            new[]
            {
                Account("a", "A", current: true, shortRemaining: 80, longRemaining: 80),
                Account("b", "B", current: false, shortRemaining: 60, longRemaining: 60)
            },
            DateTimeOffset.UtcNow));

        Assert.Equal("b", viewModel.CurrentAccountId);
        Assert.Equal("a", viewModel.CurrentThreadAccountId);
        Assert.Equal("B", viewModel.CurrentAlias);
    }

    [Theory]
    [InlineData(null, null, "b", AccountSwitchAction.PinOnly)]
    [InlineData("thread-a", "a", "a", AccountSwitchAction.PinOnly)]
    [InlineData("thread-a", "a", "b", AccountSwitchAction.MigrateThenPin)]
    public void Account_switch_policy_only_migrates_a_known_cross_account_current_thread(
        string? currentThreadId,
        string? currentThreadAccountId,
        string targetAccountId,
        AccountSwitchAction expected)
    {
        Assert.Equal(expected, AccountSwitchPolicy.Decide(currentThreadId, currentThreadAccountId, targetAccountId));
    }

    [Fact]
    public void Thread_deep_link_escapes_target_thread_id_without_launching_it()
    {
        var uri = ThreadDeepLink.Create("thread/with spaces?x");

        Assert.Equal("codex", uri.Scheme);
        Assert.Equal("threads", uri.Host);
        Assert.Equal("/thread%2Fwith%20spaces%3Fx", uri.AbsolutePath);
    }

    [Fact]
    public void View_model_prefers_general_codex_bucket_over_model_specific_bucket_for_primary_summary()
    {
        var viewModel = new OverlayViewModel();
        var snapshot = new ControlSnapshot(
            "Auto",
            null,
            "account-1",
            null,
            new[]
            {
                new ControlAccountView(
                    "account-1",
                    "A",
                    "a@example.test",
                    "plus",
                    true,
                    0,
                    "Healthy",
                    null,
                    true,
                    DateTimeOffset.UtcNow,
                    new[]
                    {
                        new ControlQuotaBucket("codex_bengalfox", "GPT-5.3-Codex-Spark", "Primary", 0, 100, 300, null),
                        new ControlQuotaBucket("codex", "Codex", "Primary", 21, 79, 10080, null)
                    })
            },
            DateTimeOffset.UtcNow);

        viewModel.Apply(snapshot);

        Assert.Equal(79, viewModel.RemainingPercent);
        Assert.Equal("79%", viewModel.RemainingText);
        var row = Assert.Single(viewModel.Accounts);
        Assert.Contains("GPT-5.3-Codex-Spark", row.SpecialQuota, StringComparison.Ordinal);
        Assert.Contains("100%", row.SpecialQuota, StringComparison.Ordinal);
        Assert.StartsWith("7d", row.LongQuota, StringComparison.Ordinal);
    }

    [Fact]
    public void Account_row_uses_dash_and_hides_progress_when_quota_is_missing()
    {
        var row = new AccountRowViewModel("a");

        row.Apply(new ControlAccountView(
            "a",
            "A",
            "a@example.test",
            "plus",
            true,
            0,
            "Healthy",
            null,
            true,
            null,
            Array.Empty<ControlQuotaBucket>()), false);

        Assert.Equal("—", row.ShortQuota);
        Assert.Equal("—", row.LongQuota);
        Assert.False(row.HasShortQuota);
        Assert.False(row.HasLongQuota);
        Assert.False(row.HasSpecialQuota);
        Assert.Equal(UiText.QuotaNeverSynced, row.QuotaSyncText);
    }

    [Fact]
    public void Failed_quota_refresh_keeps_last_known_percentages_and_marks_row_for_retry()
    {
        var viewModel = new OverlayViewModel();
        viewModel.Apply(new ControlSnapshot(
            "Auto",
            null,
            "a",
            null,
            new[] { Account("a", "A", true, 79, 68) },
            DateTimeOffset.UtcNow));

        var row = Assert.Single(viewModel.Accounts);
        var previousShort = row.ShortQuota;
        var previousLong = row.LongQuota;

        viewModel.MarkQuotaRefreshFailed("a", "network unavailable");

        Assert.Equal(previousShort, row.ShortQuota);
        Assert.Equal(previousLong, row.LongQuota);
        Assert.Equal(UiText.QuotaSyncFailed, row.QuotaSyncText);
        Assert.Equal("network unavailable", row.QuotaErrorText);
    }

    [Fact]
    public void Account_row_exposes_quota_timestamp_and_stale_refresh_decision()
    {
        var row = new AccountRowViewModel("a");
        var stale = DateTimeOffset.UtcNow.AddMinutes(-6);
        var fresh = DateTimeOffset.UtcNow.AddSeconds(-30);

        row.Apply(new ControlAccountView(
            "a", "A", "a@example.test", "plus", true, 0, "Healthy", null, true,
            stale,
            new[] { new ControlQuotaBucket("codex", "Codex", "Primary", 20, 80, 300, null) }),
            false);

        Assert.Equal(stale, row.QuotaFetchedAt);
        Assert.True(row.NeedsQuotaRefresh);

        row.Apply(new ControlAccountView(
            "a", "A", "a@example.test", "plus", true, 0, "Healthy", null, true,
            fresh,
            new[] { new ControlQuotaBucket("codex", "Codex", "Primary", 20, 80, 300, null) }),
            false);

        Assert.Equal(fresh, row.QuotaFetchedAt);
        Assert.False(row.NeedsQuotaRefresh);
    }

    [Theory]
    [InlineData("Auto", true)]
    [InlineData("Pinned", true)]
    [InlineData("Off", false)]
    public void View_model_exposes_one_product_level_routing_toggle(string mode, bool enabled)
    {
        var viewModel = new OverlayViewModel();
        viewModel.Apply(new ControlSnapshot(
            mode,
            null,
            null,
            null,
            new[] { Account("a", "A", true, 80, 80) },
            DateTimeOffset.UtcNow));

        Assert.Equal(enabled, viewModel.IsRoutingEnabled);
        Assert.Equal(enabled ? UiText.DisableRouting : UiText.EnableRouting, viewModel.RoutingToggleText);
    }

    [Fact]
    public void Official_oauth_url_is_validated_without_rewriting_any_parameter()
    {
        const string source = "https://auth.openai.com/oauth/authorize?response_type=code&client_id=app_test&redirect_uri=http%3A%2F%2Flocalhost%3A1455%2Fauth%2Fcallback&code_challenge=challenge123&code_challenge_method=S256&state=state123";

        var validated = OverlayController.ValidateOfficialLoginUrl(source);

        Assert.Equal(source, validated.OriginalString);
        Assert.DoesNotContain("prompt=", validated.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("127.0.0.1:7897", "http://127.0.0.1:7897")]
    [InlineData("http=127.0.0.1:8080;https=localhost:7897", "http://localhost:7897")]
    [InlineData("proxy.example.test:7897", null)]
    [InlineData("socks5://127.0.0.1:7897", null)]
    public void Login_proxy_detector_accepts_only_loopback_proxy_candidates(string value, string? expected)
    {
        Assert.Equal(expected, LoginProxyDetector.ParseWindowsProxyServer(value));
    }

    [Fact]
    public void Official_login_url_validator_rejects_non_openai_hosts()
    {
        Assert.Throws<ArgumentException>(() => OverlayController.ValidateOfficialLoginUrl("https://example.test/oauth/authorize?state=x"));
    }

    [Fact]
    public void Account_row_classifies_short_and_long_windows_by_duration_not_field_position()
    {
        var row = new AccountRowViewModel("a");
        row.Apply(new ControlAccountView(
            "a",
            "A",
            "a@example.test",
            "plus",
            true,
            0,
            "Healthy",
            null,
            true,
            DateTimeOffset.UtcNow,
            new[]
            {
                new ControlQuotaBucket("weekly", "Weekly", "Primary", 20, 80, 10080, null),
                new ControlQuotaBucket("short", "Short", "Secondary", 55, 45, 300, null)
            }), false);

        Assert.StartsWith("5h", row.ShortQuota, StringComparison.Ordinal);
        Assert.Contains("45%", row.ShortQuota, StringComparison.Ordinal);
        Assert.StartsWith("7d", row.LongQuota, StringComparison.Ordinal);
        Assert.Contains("80%", row.LongQuota, StringComparison.Ordinal);
    }

    private sealed class SequenceLocator : ICodexWindowLocator
    {
        private readonly CodexWindowTarget?[] _values;
        private int _index;

        public SequenceLocator(params CodexWindowTarget?[] values) => _values = values;

        public CodexWindowTarget? Find()
        {
            if (_values.Length == 0) return null;
            var index = Math.Min(_index, _values.Length - 1);
            _index++;
            return _values[index];
        }
    }

    private static ControlAccountView Account(
        string id,
        string alias,
        bool current,
        int shortRemaining,
        int longRemaining) =>
        new(
            id,
            alias,
            $"{id}@example.test",
            "plus",
            true,
            0,
            "Healthy",
            null,
            current,
            DateTimeOffset.UtcNow,
            new[]
            {
                new ControlQuotaBucket("codex", "Codex", "Primary", 100 - shortRemaining, shortRemaining, 300, DateTimeOffset.UtcNow.AddHours(2)),
                new ControlQuotaBucket("codex", "Codex", "Secondary", 100 - longRemaining, longRemaining, 10080, DateTimeOffset.UtcNow.AddDays(3))
            });
}
