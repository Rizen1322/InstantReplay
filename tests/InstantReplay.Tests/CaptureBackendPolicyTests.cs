using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class CaptureBackendPolicyTests
{
    [Theory]
    [InlineData(22000)]
    [InlineData(22621)]
    [InlineData(26100)]
    public void Windows11DefaultsToWgc(int build)
    {
        Assert.Equal(
            new CaptureBackendSelection(CaptureBackend.Wgc, Forced: false),
            CaptureBackendPolicy.SelectInitial(build, diagnosticOverride: null));
    }

    [Fact]
    public void Windows10DefaultsToDesktopDuplication()
    {
        Assert.Equal(
            new CaptureBackendSelection(CaptureBackend.DesktopDuplication, Forced: false),
            CaptureBackendPolicy.SelectInitial(19045, diagnosticOverride: null));
    }

    [Theory]
    [InlineData("wgc", CaptureBackend.Wgc)]
    [InlineData(" WGC ", CaptureBackend.Wgc)]
    [InlineData("dda", CaptureBackend.DesktopDuplication)]
    [InlineData(" DDA ", CaptureBackend.DesktopDuplication)]
    public void DiagnosticOverrideForcesBackend(string value, CaptureBackend expected)
    {
        Assert.Equal(
            new CaptureBackendSelection(expected, Forced: true),
            CaptureBackendPolicy.SelectInitial(22621, value));
    }

    [Fact]
    public void InvalidDiagnosticOverrideDoesNotOverrideWindowsDefault()
    {
        Assert.Equal(
            new CaptureBackendSelection(CaptureBackend.Wgc, Forced: false),
            CaptureBackendPolicy.SelectInitial(22621, "broken"));
    }

    [Theory]
    [InlineData(CaptureBackend.Wgc, CaptureBackend.DesktopDuplication)]
    [InlineData(CaptureBackend.DesktopDuplication, CaptureBackend.Wgc)]
    public void AlternativeReturnsOtherBackend(CaptureBackend current, CaptureBackend expected)
    {
        Assert.Equal(expected, CaptureBackendPolicy.Alternative(current));
    }
}
