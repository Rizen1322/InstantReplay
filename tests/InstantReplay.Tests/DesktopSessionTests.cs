using System.Runtime.InteropServices;
using Aura.Core.Interop;
using Aura.Core.SystemIntegration;
using Xunit;

namespace InstantReplay.Tests;

public sealed class DesktopSessionTests
{
    [Fact]
    public void WtsInfoExHeader_HasExpected64BitOffsets()
    {
        Assert.Equal(0, Marshal.OffsetOf<NativeMethods.WTSINFOEX_HEADER>(nameof(NativeMethods.WTSINFOEX_HEADER.Level)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<NativeMethods.WTSINFOEX_HEADER>(nameof(NativeMethods.WTSINFOEX_HEADER.SessionId)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<NativeMethods.WTSINFOEX_HEADER>(nameof(NativeMethods.WTSINFOEX_HEADER.SessionState)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<NativeMethods.WTSINFOEX_HEADER>(nameof(NativeMethods.WTSINFOEX_HEADER.SessionFlags)).ToInt32());
    }

    [Fact]
    public void QuerySessionLocked_LiveOsProbeDoesNotThrow()
    {
        Exception? error = Record.Exception(() => _ = DesktopSession.QuerySessionLocked());
        Assert.Null(error);
    }

    [Theory]
    [InlineData(1u, NativeMethods.WTS_ACTIVE, NativeMethods.WTS_SESSIONSTATE_LOCK, 232u, true)]
    [InlineData(1u, NativeMethods.WTS_ACTIVE, NativeMethods.WTS_SESSIONSTATE_UNLOCK, 232u, false)]
    [InlineData(1u, NativeMethods.WTS_ACTIVE, NativeMethods.WTS_SESSIONSTATE_UNKNOWN, 232u, null)]
    [InlineData(1u, 4, NativeMethods.WTS_SESSIONSTATE_UNLOCK, 232u, true)]
    [InlineData(2u, NativeMethods.WTS_ACTIVE, NativeMethods.WTS_SESSIONSTATE_LOCK, 232u, null)]
    [InlineData(1u, NativeMethods.WTS_ACTIVE, NativeMethods.WTS_SESSIONSTATE_LOCK, 16u, null)]
    public void ParseWtsSessionLocked_InterpretsFlagsAndStates(
        uint level, int sessionState, int sessionFlags, uint bytesReturned, bool? expected)
    {
        Assert.Equal(expected, DesktopSession.ParseWtsSessionLocked(level, sessionState, sessionFlags, bytesReturned));
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(true, null, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, null, false, false)]
    public void Snapshot_InteractiveDesktopActive_TruthTable(
        bool defaultDesktop, bool? sessionLocked, bool shellPresent, bool expected)
    {
        var snapshot = new DesktopSessionSnapshot(defaultDesktop, sessionLocked, shellPresent);
        Assert.Equal(expected, snapshot.InteractiveDesktopActive);
    }

    [Fact]
    public void ReadinessTracker_ImmediatelyReady_SkipsSettle()
    {
        var tracker = new DesktopReadinessTracker(
            requireShell: true,
            shellTimeout: TimeSpan.FromSeconds(20),
            settleDuration: TimeSpan.FromMilliseconds(500),
            settleIfInitiallyReady: false);

        var outcome = tracker.Observe(new DesktopSessionSnapshot(true, false, true), TimeSpan.Zero);

        Assert.Equal(DesktopReadinessOutcome.Ready, outcome);
        Assert.False(tracker.NeededWait);
    }

    [Fact]
    public void ReadinessTracker_ArsoLockOverTwoMinutes_NeverTimesOutUntilUnlockAndSettle()
    {
        var tracker = new DesktopReadinessTracker(
            requireShell: true,
            shellTimeout: TimeSpan.FromSeconds(20),
            settleDuration: TimeSpan.FromMilliseconds(500),
            settleIfInitiallyReady: false);

        var locked = new DesktopSessionSnapshot(false, true, false);
        Assert.Equal(DesktopReadinessOutcome.WaitingForInteractiveDesktop, tracker.Observe(locked, TimeSpan.Zero));
        Assert.Equal(DesktopReadinessOutcome.WaitingForInteractiveDesktop, tracker.Observe(locked, TimeSpan.FromSeconds(120)));
        Assert.Equal(DesktopReadinessOutcome.WaitingForInteractiveDesktop, tracker.Observe(locked, TimeSpan.FromSeconds(125)));
        Assert.Equal(DesktopReadinessOutcome.WaitingForInteractiveDesktop, tracker.Observe(locked, TimeSpan.FromMinutes(10)));

        var unlocked = new DesktopSessionSnapshot(true, false, true);
        Assert.Equal(DesktopReadinessOutcome.Settling, tracker.Observe(unlocked, TimeSpan.FromMinutes(10)));
        Assert.Equal(DesktopReadinessOutcome.Settling, tracker.Observe(unlocked, TimeSpan.FromMinutes(10) + TimeSpan.FromMilliseconds(499)));
        Assert.Equal(DesktopReadinessOutcome.Ready, tracker.Observe(unlocked, TimeSpan.FromMinutes(10) + TimeSpan.FromMilliseconds(500)));
        Assert.True(tracker.NeededWait);
    }

    [Fact]
    public void ReadinessTracker_RelockDuringSettle_ResetsSettleTimer()
    {
        var tracker = new DesktopReadinessTracker(
            requireShell: true,
            shellTimeout: TimeSpan.FromSeconds(20),
            settleDuration: TimeSpan.FromMilliseconds(500),
            settleIfInitiallyReady: false);

        var locked = new DesktopSessionSnapshot(false, true, true);
        var unlocked = new DesktopSessionSnapshot(true, false, true);

        Assert.Equal(DesktopReadinessOutcome.WaitingForInteractiveDesktop, tracker.Observe(locked, TimeSpan.Zero));
        Assert.Equal(DesktopReadinessOutcome.Settling, tracker.Observe(unlocked, TimeSpan.FromSeconds(10)));
        Assert.Equal(DesktopReadinessOutcome.Settling, tracker.Observe(unlocked, TimeSpan.FromSeconds(10.4)));
        Assert.Equal(DesktopReadinessOutcome.WaitingForInteractiveDesktop, tracker.Observe(locked, TimeSpan.FromSeconds(10.45)));
        Assert.Equal(DesktopReadinessOutcome.Settling, tracker.Observe(unlocked, TimeSpan.FromSeconds(11.0)));
        Assert.Equal(DesktopReadinessOutcome.Settling, tracker.Observe(unlocked, TimeSpan.FromSeconds(11.4)));
        Assert.Equal(DesktopReadinessOutcome.Ready, tracker.Observe(unlocked, TimeSpan.FromSeconds(11.5)));
    }

    [Fact]
    public void ReadinessTracker_AlternativeShell_TimeoutStartsAfterUnlockAndResetsOnRelock()
    {
        var tracker = new DesktopReadinessTracker(
            requireShell: true,
            shellTimeout: TimeSpan.FromSeconds(20),
            settleDuration: TimeSpan.FromMilliseconds(500),
            settleIfInitiallyReady: false);

        var locked = new DesktopSessionSnapshot(false, true, false);
        var unlockedNoShell = new DesktopSessionSnapshot(true, false, false);

        Assert.Equal(DesktopReadinessOutcome.WaitingForInteractiveDesktop, tracker.Observe(locked, TimeSpan.Zero));
        Assert.Equal(DesktopReadinessOutcome.WaitingForInteractiveDesktop, tracker.Observe(locked, TimeSpan.FromSeconds(150)));
        Assert.Equal(DesktopReadinessOutcome.WaitingForShell, tracker.Observe(unlockedNoShell, TimeSpan.FromSeconds(150)));
        Assert.Equal(DesktopReadinessOutcome.WaitingForShell, tracker.Observe(unlockedNoShell, TimeSpan.FromSeconds(165)));
        Assert.Equal(DesktopReadinessOutcome.WaitingForInteractiveDesktop, tracker.Observe(locked, TimeSpan.FromSeconds(166)));
        Assert.Equal(DesktopReadinessOutcome.WaitingForShell, tracker.Observe(unlockedNoShell, TimeSpan.FromSeconds(170)));
        Assert.Equal(DesktopReadinessOutcome.WaitingForShell, tracker.Observe(unlockedNoShell, TimeSpan.FromSeconds(189)));
        Assert.Equal(DesktopReadinessOutcome.Settling, tracker.Observe(unlockedNoShell, TimeSpan.FromSeconds(190)));
        Assert.Equal(DesktopReadinessOutcome.Settling, tracker.Observe(unlockedNoShell, TimeSpan.FromSeconds(190.4)));
        Assert.Equal(DesktopReadinessOutcome.ReadyWithoutShell, tracker.Observe(unlockedNoShell, TimeSpan.FromSeconds(190.5)));
    }

    [Fact]
    public void ReadinessTracker_SettleIfInitiallyReady_EnforcesSettleEvenWhenUnlockedImmediately()
    {
        var tracker = new DesktopReadinessTracker(
            requireShell: false,
            shellTimeout: TimeSpan.Zero,
            settleDuration: TimeSpan.FromMilliseconds(500),
            settleIfInitiallyReady: true);

        var unlocked = new DesktopSessionSnapshot(true, false, false);
        Assert.Equal(DesktopReadinessOutcome.Settling, tracker.Observe(unlocked, TimeSpan.Zero));
        Assert.True(tracker.NeededWait);
        Assert.Equal(DesktopReadinessOutcome.Settling, tracker.Observe(unlocked, TimeSpan.FromMilliseconds(499)));
        Assert.Equal(DesktopReadinessOutcome.Ready, tracker.Observe(unlocked, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task ReadinessWaiter_WakeSignal_WakesImmediately()
    {
        var wake = new DesktopWakeSignal();
        object sync = new();
        var current = new DesktopSessionSnapshot(false, true, false);
        var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<DesktopWaitResult> waitTask = DesktopReadinessWaiter.WaitAsync(
            () =>
            {
                lock (sync)
                    return current;
            },
            requireShell: true,
            shellTimeout: TimeSpan.FromSeconds(20),
            settleDuration: TimeSpan.FromMilliseconds(25),
            pollInterval: TimeSpan.FromSeconds(30),
            settleIfInitiallyReady: false,
            onFirstWait: _ => waitStarted.TrySetResult(),
            wake: wake);

        await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        lock (sync)
            current = new DesktopSessionSnapshot(true, false, true);
        wake.Pulse();

        DesktopWaitResult result = await waitTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(result.Aborted);
        Assert.True(result.Waited);
        Assert.Equal(DesktopReadinessOutcome.Ready, result.Outcome);
    }

    [Fact]
    public async Task ReadinessWaiter_CancellationAndShouldAbort_ReturnAbortedCleanly()
    {
        using var cts = new CancellationTokenSource();
        var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<DesktopWaitResult> cancelWait = DesktopReadinessWaiter.WaitAsync(
            () => new DesktopSessionSnapshot(false, true, false),
            requireShell: false,
            shellTimeout: TimeSpan.Zero,
            settleDuration: TimeSpan.FromMilliseconds(25),
            pollInterval: TimeSpan.FromSeconds(30),
            onFirstWait: _ => waitStarted.TrySetResult(),
            ct: cts.Token);

        await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cts.Cancel();
        DesktopWaitResult cancelledResult = await cancelWait.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(cancelledResult.Aborted);

        bool abort = false;
        var abortWake = new DesktopWakeSignal();
        var abortStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<DesktopWaitResult> abortWait = DesktopReadinessWaiter.WaitAsync(
            () => new DesktopSessionSnapshot(false, true, false),
            requireShell: false,
            shellTimeout: TimeSpan.Zero,
            settleDuration: TimeSpan.FromMilliseconds(25),
            pollInterval: TimeSpan.FromSeconds(30),
            onFirstWait: _ => abortStarted.TrySetResult(),
            shouldAbort: () => Volatile.Read(ref abort),
            wake: abortWake);

        await abortStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Volatile.Write(ref abort, true);
        abortWake.Pulse();
        DesktopWaitResult abortedResult = await abortWait.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(abortedResult.Aborted);
    }
}
