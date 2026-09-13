namespace Aura.Core.Capture;

public enum CaptureBackend
{
    Wgc,
    WgcWindow,
    DesktopDuplication,
    MinecraftOpenGl
}

internal readonly record struct CaptureBackendSelection(CaptureBackend Backend, bool Forced);

internal readonly record struct CaptureBackendTargetSelection(
    CaptureBackend Backend,
    GameCaptureTarget? Target,
    bool RestartRequired);

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

    /// <summary>
    /// Minecraft OpenGL выбирается до ожидания watchdog. Уже работающий гибридный
    /// provider переживает Alt-Tab сам; пересборка нужна только при новом процессе.
    /// Diagnostic override намеренно запрещает инъекцию.
    /// </summary>
    public static CaptureBackendTargetSelection SelectForForeground(
        CaptureBackend activeBackend,
        GameCaptureTarget? activeTarget,
        GameCaptureTarget? foregroundTarget,
        bool forcedBackend)
    {
        if (forcedBackend)
            return new(activeBackend, activeTarget, RestartRequired: false);

        if (activeBackend == CaptureBackend.MinecraftOpenGl)
        {
            if (foregroundTarget is GameCaptureTarget replacement &&
                IsMinecraftOpenGlTarget(replacement) &&
                (activeTarget is not GameCaptureTarget current ||
                 !current.HasSameIdentity(replacement)))
            {
                return new(CaptureBackend.MinecraftOpenGl, replacement, RestartRequired: true);
            }

            return new(CaptureBackend.MinecraftOpenGl, activeTarget, RestartRequired: false);
        }

        return foregroundTarget is GameCaptureTarget minecraft &&
               IsMinecraftOpenGlTarget(minecraft)
            ? new(CaptureBackend.MinecraftOpenGl, minecraft, RestartRequired: true)
            : new(activeBackend, activeTarget, RestartRequired: false);
    }

    public static bool IsMinecraftOpenGlTarget(in GameCaptureTarget target)
    {
        string executable = Path.GetFileName(target.ExecutableName.Trim());
        if (!Path.HasExtension(executable)) executable += ".exe";
        return executable.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase) &&
               target.GameName.Equals("Minecraft", StringComparison.OrdinalIgnoreCase);
    }
}
