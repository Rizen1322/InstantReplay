using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class CaptureProcessingWatchdogTests
{
    [Fact]
    public void Accepted_capture_with_failing_conversion_triggers_recovery()
    {
        var watchdog = new CaptureProcessingWatchdog();
        for (int second = 0; second < 6; second++)
            Assert.False(watchdog.Observe(second * 60, 1, TimeSpan.FromSeconds(second), false));
        Assert.True(watchdog.Observe(360, 1, TimeSpan.FromSeconds(6), false));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Static_screen_processing_progress_and_full_queue_are_healthy(
        bool staticScreen, bool processing, bool saturated)
    {
        var watchdog = new CaptureProcessingWatchdog();
        for (int second = 0; second < 20; second++)
            Assert.False(watchdog.Observe(staticScreen ? 100 : second * 60,
                processing ? second : 0, TimeSpan.FromSeconds(second), saturated));
    }

    [Fact]
    public void Recovery_resets_the_five_second_window()
    {
        var watchdog = new CaptureProcessingWatchdog();
        for (int second = 0; second < 5; second++)
            Assert.False(watchdog.Observe(second * 60, 0, TimeSpan.FromSeconds(second), false));
        Assert.False(watchdog.Observe(300, 1, TimeSpan.FromSeconds(5), false));
        Assert.False(watchdog.Observe(360, 1, TimeSpan.FromSeconds(6), false));
    }
}
