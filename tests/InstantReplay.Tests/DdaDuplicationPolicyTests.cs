using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class DdaDuplicationPolicyTests
{
    [Theory]
    [InlineData(unchecked((int)0x887A0001))] // DXGI_ERROR_INVALID_CALL: предыдущий кадр не освобождён
    [InlineData(unchecked((int)0x887A0026))] // DXGI_ERROR_ACCESS_LOST: смена fullscreen/desktop mode
    public void BrokenFrameSessionMustBeRecreated(int hresult) =>
        Assert.True(DdaDuplicationPolicy.ShouldRecreateFrameSession(hresult));

    [Theory]
    [InlineData(unchecked((int)0x887A0027))] // DXGI_ERROR_WAIT_TIMEOUT: на статичном экране это нормально
    [InlineData(unchecked((int)0x80070057))] // E_INVALIDARG: программная ошибка, не зацикливаем recovery
    public void NonLifecycleFrameResultsMustNotRecreateSession(int hresult) =>
        Assert.False(DdaDuplicationPolicy.ShouldRecreateFrameSession(hresult));

    [Theory]
    [InlineData(unchecked((int)0x80004002))]
    [InlineData(unchecked((int)0x80004001))]
    [InlineData(unchecked((int)0x80070057))] // E_INVALIDARG из DuplicateOutput1 на Windows 10
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
