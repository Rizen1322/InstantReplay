using Aura.Core.Encoding;
using Xunit;

namespace InstantReplay.Tests;

public sealed class NvencStatsTests
{
    [Fact]
    public void Since_reports_only_what_happened_after_the_snapshot()
    {
        var start = new NvencStats.Snapshot(1, 2, 3, 4, 5, 6, 7);
        var end = new NvencStats.Snapshot(11, 2, 5, 4, 8, 6, 9);

        Assert.Equal(new NvencStats.Snapshot(10, 0, 2, 0, 3, 0, 2), end.Since(start));
    }

    [Fact]
    public void Line_names_every_counter()
    {
        string line = new NvencStats.Snapshot(1, 2, 3, 4, 5, 6, 7).ToString();

        Assert.Equal("nvencBusyCount=1, nvencNeedMoreInputCount=2, nvencDroppedInputFrames=3, " +
                     "nvencDroppedOutputFrames=4, nvencForcedIdrCount=5, nvencFatalErrors=6, nvencFallbackCount=7",
                     line);
    }
}
