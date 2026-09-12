using Aura.Core.Engine;
using Xunit;

namespace InstantReplay.Tests;

public sealed class CaptureRecoveryBackoffTests
{
    [Theory]
    [InlineData(1, false, 250)]
    [InlineData(1, true, 1500)]
    [InlineData(2, false, 3000)]
    [InlineData(3, false, 5000)]
    [InlineData(4, false, 10000)]
    [InlineData(5, false, 15000)]
    [InlineData(50, false, 15000)]
    public void RecoveryBackoffIsBounded(
        int attempt, bool deviceLost, int expected) =>
        Assert.Equal(expected, CaptureRecoveryBackoff.DelayMilliseconds(attempt, deviceLost));
}
