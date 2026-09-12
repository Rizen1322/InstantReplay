using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class DdaLifecycleMonitorTests
{
    [Fact]
    public void Starts_stable()
    {
        var monitor = CreateMonitor();

        Assert.Equal(DdaLifecycleState.Stable, monitor.State);
    }

    [Fact]
    public void One_invalidation_holds_transition_frames_for_two_seconds()
    {
        var monitor = CreateMonitor();

        Assert.Equal(
            DdaLifecycleState.TransitionHold,
            monitor.RecordInvalidation(TimeSpan.Zero));
        Assert.Equal(
            DdaLifecycleState.TransitionHold,
            monitor.ObserveUsefulFrame(TimeSpan.FromMilliseconds(1999)));
        Assert.Equal(
            DdaLifecycleState.Stable,
            monitor.ObserveUsefulFrame(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Three_invalidations_within_two_seconds_form_a_storm()
    {
        var monitor = CreateMonitor();

        monitor.RecordInvalidation(TimeSpan.Zero);
        monitor.RecordInvalidation(TimeSpan.FromMilliseconds(700));

        Assert.Equal(
            DdaLifecycleState.Storm,
            monitor.RecordInvalidation(TimeSpan.FromMilliseconds(1400)));
    }

    [Fact]
    public void Invalidations_older_than_storm_window_expire()
    {
        var monitor = CreateMonitor();

        monitor.RecordInvalidation(TimeSpan.Zero);
        monitor.RecordInvalidation(TimeSpan.FromMilliseconds(800));

        Assert.Equal(
            DdaLifecycleState.TransitionHold,
            monitor.RecordInvalidation(TimeSpan.FromMilliseconds(2100)));
    }

    [Fact]
    public void Stable_run_clears_previous_invalidations()
    {
        var monitor = CreateMonitor();
        monitor.RecordInvalidation(TimeSpan.Zero);
        Assert.Equal(
            DdaLifecycleState.Stable,
            monitor.ObserveUsefulFrame(TimeSpan.FromSeconds(2)));

        Assert.Equal(
            DdaLifecycleState.TransitionHold,
            monitor.RecordInvalidation(TimeSpan.FromMilliseconds(2100)));
        Assert.Equal(1, monitor.InvalidationsInWindow);
    }

    [Fact]
    public void Storm_remains_quarantined_until_reset()
    {
        var monitor = CreateMonitor();
        monitor.RecordInvalidation(TimeSpan.Zero);
        monitor.RecordInvalidation(TimeSpan.FromMilliseconds(500));
        monitor.RecordInvalidation(TimeSpan.FromSeconds(1));

        Assert.Equal(
            DdaLifecycleState.Storm,
            monitor.ObserveUsefulFrame(TimeSpan.FromSeconds(10)));

        monitor.Reset();
        Assert.Equal(DdaLifecycleState.Stable, monitor.State);
        Assert.Equal(0, monitor.InvalidationsInWindow);
    }

    private static DdaLifecycleMonitor CreateMonitor() => new(
        stormWindow: TimeSpan.FromSeconds(2),
        stableWindow: TimeSpan.FromSeconds(2),
        stormThreshold: 3);
}
