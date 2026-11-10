using System.Diagnostics;

namespace Aura.Core.Capture;

/// <summary>
/// Рисует ли игра на переднем плане через OpenGL (см. CaptureBackendPolicy.SelectForForeground).
/// Список модулей чужого процесса читается долго, а спрашивают раз в две секунды,
/// поэтому ответ запоминается на процесс; «нет» перепроверяется раз в 10 секунд —
/// opengl32.dll может загрузиться уже после появления окна.
/// </summary>
internal static class OpenGlProcessProbe
{
    private static readonly object Sync = new();
    private static (int Pid, long Start, bool OpenGl, long CheckedMs) _last;

    /// <summary>Жив ли тот самый процесс: PID мог достаться другому, поэтому сверяем и время запуска.</summary>
    public static bool IsAlive(int processId, long startTicks)
    {
        if (processId <= 0) return false;
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch { return false; }
    }

    public static bool UsesOpenGl(GameCaptureTarget? target)
    {
        if (target is not GameCaptureTarget game) return false;
        lock (Sync)
        {
            long now = Environment.TickCount64;
            if (_last.Pid == game.ProcessId && _last.Start == game.ProcessStartTicks &&
                (_last.OpenGl || now - _last.CheckedMs < 10_000))
                return _last.OpenGl;

            bool openGl = false;
            try
            {
                using Process process = Process.GetProcessById(game.ProcessId);
                openGl = CaptureBackendPolicy.UsesOpenGl(
                    process.Modules.Cast<ProcessModule>().Select(m => m.ModuleName ?? ""));
            }
            catch { /* защищённый или уже закрытый процесс: считаем, что не OpenGL */ }

            _last = (game.ProcessId, game.ProcessStartTicks, openGl, now);
            return openGl;
        }
    }
}
