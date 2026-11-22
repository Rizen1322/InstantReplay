namespace Aura.Core.Capture;

public enum CaptureBackend
{
    Wgc,
    DesktopDuplication = 2,
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
            "Для смены источника нужен мониторный захват",
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
        bool forcedBackend,
        CaptureEpisode episode = default,
        bool foregroundUsesOpenGl = false)
    {
        if (forcedBackend)
            return new(activeBackend, activeTarget, RestartRequired: false);

        if (activeBackend == CaptureBackend.MinecraftOpenGl)
        {
            if (foregroundTarget is GameCaptureTarget replacement &&
                IsMinecraftOpenGlTarget(replacement) && !HookQuarantined(replacement, episode) &&
                (activeTarget is not GameCaptureTarget current ||
                 !current.HasSameIdentity(replacement)))
            {
                return new(CaptureBackend.MinecraftOpenGl, replacement, RestartRequired: true);
            }

            return new(CaptureBackend.MinecraftOpenGl, activeTarget, RestartRequired: false);
        }

        if (foregroundTarget is GameCaptureTarget minecraft && IsMinecraftOpenGlTarget(minecraft) &&
            !HookQuarantined(minecraft, episode))
            return new(CaptureBackend.MinecraftOpenGl, minecraft, RestartRequired: true);

        // WGC на время записи переводит систему на программный курсор, а OpenGL-игра
        // на весь экран (Project Zomboid) идёт мимо композитора, и курсор в ней
        // пропадает с экрана, пока Aura пишет. У Desktop Duplication этого нет
        // (microsoft/Windows.UI.Composition-Win32-Samples#128). Обратно на WGC не
        // возвращаемся до следующего включения повтора: игру сворачивают и разворачивают.
        if (activeBackend == CaptureBackend.Wgc && foregroundUsesOpenGl &&
            foregroundTarget is GameCaptureTarget openGl)
            return new(CaptureBackend.DesktopDuplication, openGl, RestartRequired: true);

        return new(activeBackend, activeTarget, RestartRequired: false);
    }

    private static bool HookQuarantined(in GameCaptureTarget target, in CaptureEpisode episode) =>
        episode.Matches(target) && episode.IsQuarantined(CaptureBackend.MinecraftOpenGl);

    /// <summary>
    /// Пора ли вернуться на WGC после записи OpenGL-игры через Desktop Duplication:
    /// только когда её процесс завершился и на экране нет другой такой же игры.
    /// </summary>
    public static bool ShouldReturnToWgc(
        CaptureBackend active, CaptureBackend preferred, bool forcedBackend,
        bool avoidingWgc, bool gameAlive, bool foregroundUsesOpenGl) =>
        avoidingWgc && !forcedBackend && !gameAlive && !foregroundUsesOpenGl &&
        active == CaptureBackend.DesktopDuplication && preferred == CaptureBackend.Wgc;

    /// <summary>Игра рисует через OpenGL: по списку загруженных в её процесс модулей.</summary>
    public static bool UsesOpenGl(IEnumerable<string> loadedModules) =>
        loadedModules.Any(name => name.Equals("opengl32.dll", StringComparison.OrdinalIgnoreCase));

    public static bool IsMinecraftOpenGlTarget(in GameCaptureTarget target)
    {
        string executable = Path.GetFileName(target.ExecutableName.Trim());
        if (!Path.HasExtension(executable)) executable += ".exe";
        return executable.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase) &&
               target.GameName.Equals("Minecraft", StringComparison.OrdinalIgnoreCase);
    }
}
