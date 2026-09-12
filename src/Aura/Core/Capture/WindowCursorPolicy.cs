namespace Aura.Core.Capture;

internal readonly record struct CursorDrawPosition(int X, int Y);

internal static class WindowCursorPolicy
{
    public static CursorDrawPosition MapPosition(
        int screenX,
        int screenY,
        int clientLeft,
        int clientTop,
        int hotspotX,
        int hotspotY) => new(
            checked(screenX - clientLeft - hotspotX),
            checked(screenY - clientTop - hotspotY));

    public static bool IsInsideTarget(int screenX, int screenY, in PixelRect clientBounds) =>
        clientBounds.Width > 0 &&
        clientBounds.Height > 0 &&
        screenX >= clientBounds.X &&
        screenY >= clientBounds.Y &&
        (long)screenX < (long)clientBounds.X + clientBounds.Width &&
        (long)screenY < (long)clientBounds.Y + clientBounds.Height;

    public static bool ShouldRefreshShape(
        nint previousHandle,
        nint currentHandle,
        bool hasCachedShape) =>
        !hasCachedShape || currentHandle == 0 || currentHandle != previousHandle;

    public static bool ShouldReset(long previousRevision, long currentRevision) =>
        previousRevision <= 0 || previousRevision != currentRevision;
}
