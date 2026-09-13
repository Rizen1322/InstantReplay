using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class ScreenshotFramePolicyTests
{
    [Fact]
    public void FullMonitorFrameCanBeUsedForDesktopScreenshot() =>
        Assert.True(ScreenshotFramePolicy.CanUseLiveFrame(CaptureSurfaceScope.Monitor));

    [Fact]
    public void GameWindowFrameCannotBeUsedForDesktopScreenshot() =>
        Assert.False(ScreenshotFramePolicy.CanUseLiveFrame(CaptureSurfaceScope.GameWindow));
}
