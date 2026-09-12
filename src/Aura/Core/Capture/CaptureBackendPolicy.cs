namespace Aura.Core.Capture;

public enum CaptureBackend
{
    Wgc,
    WgcWindow,
    DesktopDuplication
}

internal readonly record struct CaptureBackendSelection(CaptureBackend Backend, bool Forced);

/// <summary>
/// Чистая логика выбора источника: пользователю не нужно знать о WGC/DDA,
/// а диагностическая переменная сохраняет возможность воспроизводимых тестов.
/// </summary>
internal static class CaptureBackendPolicy
{
    public static CaptureBackendSelection SelectInitial(int windowsBuild, string? diagnosticOverride)
    {
        string forced = diagnosticOverride?.Trim().ToLowerInvariant() ?? "";
        if (forced == "wgc") return new(CaptureBackend.Wgc, Forced: true);
        if (forced == "dda") return new(CaptureBackend.DesktopDuplication, Forced: true);

        return new(
            windowsBuild >= 22000 ? CaptureBackend.Wgc : CaptureBackend.DesktopDuplication,
            Forced: false);
    }

    public static CaptureBackend Alternative(CaptureBackend backend) => backend switch
    {
        CaptureBackend.Wgc => CaptureBackend.DesktopDuplication,
        CaptureBackend.DesktopDuplication => CaptureBackend.Wgc,
        _ => throw new ArgumentException(
            "Оконный WGC не участвует в попарном выборе мониторных источников",
            nameof(backend))
    };
}
