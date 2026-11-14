using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class MonitorSelectionTests
{
    [Fact]
    public void Stored_identity_survives_reordered_displays() =>
        Assert.Equal(0, MonitorSelection.Resolve(["second", "first"], 1, "SECOND"));

    [Fact]
    public void Unplugged_display_never_falls_back_to_another_screen() =>
        Assert.Throws<InvalidOperationException>(() => MonitorSelection.Resolve(["first"], 0, "second"));

    [Fact]
    public void Legacy_settings_use_index_only_until_identity_is_saved() =>
        Assert.Equal(1, MonitorSelection.Resolve(["first", "second"], 1, null));

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Invalid_legacy_index_does_not_clamp(int index) =>
        Assert.Throws<InvalidOperationException>(() => MonitorSelection.Resolve(["first"], index, null));
}
