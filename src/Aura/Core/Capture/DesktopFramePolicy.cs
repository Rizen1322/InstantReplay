namespace Aura.Core.Capture;

/// <summary>Определяет, несёт ли кадр DDA видимое изменение для записи.</summary>
internal static class DesktopFramePolicy
{
    /// <param name="cursorChanged">
    /// Изменилась ли от обновления курсора сама картинка (см.
    /// <c>DdaCursorTracker.Track</c>): видимый курсор сдвинулся или сменил форму,
    /// либо видимость переключилась. Перемещение СКРЫТОГО курсора сюда не входит —
    /// он в кадре не виден, и кадр с пустой картинкой писать не нужно.
    /// </param>
    public static bool ShouldCapture(uint accumulatedFrames, bool firstFrameSinceStart,
                                     bool cursorChanged) =>
        accumulatedFrames > 0 || firstFrameSinceStart || cursorChanged;
}
