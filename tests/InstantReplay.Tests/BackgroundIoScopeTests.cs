using Aura.Core.Interop;
using Aura.Core.Saving;
using Xunit;

namespace InstantReplay.Tests;

public class BackgroundIoScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Writer_releases_background_before_flush_and_restores_priority(bool failWrite)
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                using var output = new MemoryStream();
                using (var stream = new ReplaySaver.GentleWriteStream(output, gentle: true))
                {
                    Assert.False(BackgroundIoScope.IsActive);
                    if (failWrite)
                    {
                        output.Dispose();
                        Assert.Throws<ObjectDisposedException>(() => stream.Write(new byte[1]));
                        Assert.True(BackgroundIoScope.IsActive);
                    }
                    else
                    {
                        stream.Write(new byte[1]);
                        Assert.True(BackgroundIoScope.IsActive);
                        Assert.True(stream.FirstWriteMs >= 0);
                        Assert.True(stream.WriteElapsedMs >= 0);
                        stream.FinishIo();
                        Assert.False(BackgroundIoScope.IsActive);
                        Assert.Equal(ThreadPriority.Normal, Thread.CurrentThread.Priority);
                        stream.Flush();
                    }
                }
                Assert.False(BackgroundIoScope.IsActive);
                Assert.Equal(ThreadPriority.BelowNormal, Thread.CurrentThread.Priority);
            }
            catch (Exception ex) { failure = ex; }
            finally { BackgroundIoScope.ReleaseForCurrentThread(); }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
    }

    [Fact]
    public void Failed_exit_keeps_state_and_can_be_retried()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Assert.False(BackgroundIoScope.EnterIf(false));
                Assert.False(BackgroundIoScope.ReleaseForCurrentThread());
                Assert.True(BackgroundIoScope.EnterIf(true));
                Assert.False(BackgroundIoScope.EnterIf(true));
                Assert.True(NativeMethods.SetThreadPriority(NativeMethods.GetCurrentThread(),
                    NativeMethods.THREAD_MODE_BACKGROUND_END));
                Assert.False(BackgroundIoScope.ReleaseForCurrentThread());
                Assert.True(BackgroundIoScope.IsActive);
                Assert.True(NativeMethods.SetThreadPriority(NativeMethods.GetCurrentThread(),
                    NativeMethods.THREAD_MODE_BACKGROUND_BEGIN));
                Assert.True(BackgroundIoScope.ReleaseForCurrentThread());
                Assert.False(BackgroundIoScope.IsActive);
                Assert.False(BackgroundIoScope.ReleaseForCurrentThread());
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                NativeMethods.SetThreadPriority(NativeMethods.GetCurrentThread(),
                    NativeMethods.THREAD_MODE_BACKGROUND_END);
            }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
    }
}
