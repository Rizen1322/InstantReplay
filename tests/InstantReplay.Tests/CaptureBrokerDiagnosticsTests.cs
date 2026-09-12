using Aura.Core.Diagnostics;
using Xunit;

namespace InstantReplay.Tests;

public sealed class CaptureBrokerDiagnosticsTests
{
    [Theory]
    [InlineData(10_000_000, 9_900_000, 10)]
    [InlineData(10_000_000, 10_100_000, 0)]
    [InlineData(10_000_000, 0, 0)]
    public void FrameAgeUsesOnlyQpcHundredNanosecondTicks(
        long now, long latest, long expectedMilliseconds) =>
        Assert.Equal(expectedMilliseconds,
            CaptureBrokerDiagnostics.AgeMilliseconds(now, latest));
}
