using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class DdaDuplicationPolicyTests
{
    [Theory]
    [InlineData(unchecked((int)0x80004002))]
    [InlineData(unchecked((int)0x80004001))]
    [InlineData(unchecked((int)0x887A0004))]
    public void CapabilityFailuresMayFallBack(int hresult) =>
        Assert.True(DdaDuplicationPolicy.ShouldFallBackToLegacy(hresult));

    [Theory]
    [InlineData(unchecked((int)0x80070005))]
    [InlineData(unchecked((int)0x887A0022))]
    [InlineData(unchecked((int)0x887A0028))]
    public void AccessFailuresMustReachRecovery(int hresult) =>
        Assert.False(DdaDuplicationPolicy.ShouldFallBackToLegacy(hresult));
}
