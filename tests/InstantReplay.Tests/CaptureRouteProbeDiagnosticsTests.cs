using Aura.Core.Capture.GameHook;
using Aura.Core.Diagnostics;
using Xunit;

namespace InstantReplay.Tests;

public sealed class CaptureRouteProbeDiagnosticsTests
{
    [Theory]
    [InlineData((int)GameCaptureRoute.Monitor, "WGC-monitor")]
    [InlineData((int)GameCaptureRoute.GamePending, "OpenGL-game-pending")]
    [InlineData((int)GameCaptureRoute.GameLive, "OpenGL-game-live")]
    public void Hybrid_route_has_unambiguous_probe_label(
        int route,
        string expected)
    {
        var diagnostics = new CaptureRouteProbeDiagnostics(
            (GameCaptureRoute)route, 3, 4, 5, 6, 7, 8, 9, 10, 11,
            GameHookState.Capturing,
            GameHookError.None);

        Assert.Equal(expected, diagnostics.Label);
    }
}
