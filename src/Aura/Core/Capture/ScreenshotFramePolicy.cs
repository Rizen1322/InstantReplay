namespace Aura.Core.Capture;

internal static class ScreenshotFramePolicy
{
    // Обычный скриншот всегда означает весь рабочий стол выбранного монитора.
    // Кадр WGC-window/OpenGL-game нельзя подставлять даже при совпадении монитора:
    // иначе получится игра, растянутая на экран, либо содержимое под другими окнами.
    public static bool CanUseLiveFrame(CaptureSurfaceScope scope) =>
        scope == CaptureSurfaceScope.Monitor;
}
