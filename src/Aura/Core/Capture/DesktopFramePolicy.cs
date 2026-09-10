namespace Aura.Core.Capture;

/// <summary>Определяет, несёт ли кадр DDA видимое изменение для записи.</summary>
internal static class DesktopFramePolicy
{
    public static bool ShouldCapture(uint accumulatedFrames, bool firstFrameSinceStart,
                                     bool cursorChanged) =>
        accumulatedFrames > 0 || firstFrameSinceStart || cursorChanged;
}
