using CodexRouter.Control;
using CodexRouter.Overlay;
using Xunit;

namespace CodexRouter.Overlay.Tests;

public sealed class PillPresentationTests
{
    [Fact]
    public void Pill_height_matches_codex_title_bar_at_125_percent_scaling()
    {
        var target = new CodexWindowTarget(
            new IntPtr(1),
            123,
            new NativeWindowRect(0, 0, 1600, 900),
            120,
            false,
            true);

        var placement = OverlayPlacementCalculator.CalculatePhysical(target, 180, 32);

        Assert.Equal(225, placement.Width);
        Assert.Equal(40, placement.Height);
    }

    [Fact]
    public void Redesigned_pill_dimensions_are_positioned_in_physical_pixels_at_target_dpi()
    {
        var target = new CodexWindowTarget(
            new IntPtr(1),
            123,
            new NativeWindowRect(150, 300, 1650, 1350),
            144,
            false,
            true);

        var placement = OverlayPlacementCalculator.CalculatePhysical(target, 180, 32);

        Assert.Equal(480, placement.Left);
        Assert.Equal(308, placement.Top);
        Assert.Equal(270, placement.Width);
        Assert.Equal(48, placement.Height);
    }

    [Fact]
    public void Empty_snapshot_uses_a_purposeful_signed_out_presentation()
    {
        var viewModel = new OverlayViewModel();

        viewModel.Apply(new ControlSnapshot(
            "Auto",
            null,
            null,
            null,
            Array.Empty<ControlAccountView>(),
            DateTimeOffset.UtcNow));

        Assert.Equal(UiText.NotSignedIn, viewModel.PillAccountName);
        Assert.Equal("C", viewModel.AccountInitial);
    }

    [Fact]
    public void Selected_account_presentation_prefers_alias_and_exposes_one_avatar_character()
    {
        var viewModel = new OverlayViewModel();
        var account = new ControlAccountView(
            "account-1",
            "神机工作号",
            "user@example.test",
            "plus",
            true,
            0,
            "Healthy",
            null,
            true,
            DateTimeOffset.UtcNow,
            Array.Empty<ControlQuotaBucket>());

        viewModel.Apply(new ControlSnapshot(
            "Auto",
            null,
            "account-1",
            null,
            new[] { account },
            DateTimeOffset.UtcNow));

        Assert.Equal("神机工作号", viewModel.PillAccountName);
        Assert.Equal("神", viewModel.AccountInitial);
    }

    [Theory]
    [InlineData("  Alice  ", "Alice", "A")]
    [InlineData("", "user@example.test", "U")]
    public void Presentation_normalizes_alias_and_falls_back_to_email(
        string alias,
        string expectedName,
        string expectedInitial)
    {
        var viewModel = new OverlayViewModel();
        var account = new ControlAccountView(
            "account-1",
            alias,
            "user@example.test",
            "plus",
            true,
            0,
            "Healthy",
            null,
            true,
            DateTimeOffset.UtcNow,
            Array.Empty<ControlQuotaBucket>());

        viewModel.Apply(new ControlSnapshot(
            "Auto",
            null,
            "account-1",
            null,
            new[] { account },
            DateTimeOffset.UtcNow));

        Assert.Equal(expectedName, viewModel.PillAccountName);
        Assert.Equal(expectedInitial, viewModel.AccountInitial);
    }
}
