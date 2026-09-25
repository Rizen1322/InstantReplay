using System.Diagnostics;
using Aura.Core.Interop;
using Aura.Core.Logging;

namespace Aura.Core.GameDetection;

/// <summary>
/// Определение игры по активному окну.
/// Порядок: своё окно и заведомо неигровые программы → «Desktop», затем словарь
/// известных игр, затем название из ресурсов exe, затем имя процесса.
/// Результат — имя папки: Videos\Aura\&lt;Игра&gt;\replay_*.mp4.
///
/// Сами списки живут в <see cref="GameDatabase"/> и дополняются файлом games.json.
///
/// РЕЗУЛЬТАТ КЭШИРУЕТСЯ. Движок спрашивает игру раз в две секунды всё время работы
/// буфера, а разрешение имени для незнакомой программы стоит дорого: MainModule
/// перечисляет модули процесса, FileVersionInfo открывает exe и читает его ресурсы.
/// Пока на переднем плане тот же процесс, ответ берётся из кэша.
/// </summary>
public static class GameDetector
{
    private static GameDatabase _database = GameDatabase.Load();

    /// <summary>Своё имя процесса: окно самой Aura игрой считать нельзя ни при каком раскладе.</summary>
    private static readonly string SelfProcess = GetSelfName();

    // Кэш последнего ответа. Ключ — пара (pid, имя exe): номера процессов система
    // переиспользует, а вот совпадение номера И имени на другой программе — случай,
    // которым можно пренебречь.
    private static readonly object _cacheSync = new();
    private static uint _cachedPid;
    private static string _cachedExe = "";
    private static string _cachedName = "";

    private static string GetSelfName()
    {
        try { return Process.GetCurrentProcess().ProcessName; }
        catch { return "aura"; }
    }

    /// <summary>Перечитать games.json и сбросить кэш — после правки списков вручную.</summary>
    public static void ReloadDatabase()
    {
        _database = GameDatabase.Load();
        lock (_cacheSync) { _cachedPid = 0; _cachedExe = ""; _cachedName = ""; }
    }

    public static string DetectForegroundGame()
    {
        try
        {
            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "Desktop";
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "Desktop";

            using var proc = Process.GetProcessById((int)pid);
            string exe = proc.ProcessName;

            lock (_cacheSync)
                if (pid == _cachedPid && string.Equals(exe, _cachedExe, StringComparison.OrdinalIgnoreCase))
                    return _cachedName;

            string? name = Resolve(proc, exe, hwnd);

            // null: окно пока не похоже на игру (не на весь экран и не из папки
            // игр). Не кэшируем: игра часто стартует окном и через секунду
            // разворачивается, и ответ должен смениться вместе с ней.
            if (name is null) return "Desktop";
            lock (_cacheSync) { _cachedPid = pid; _cachedExe = exe; _cachedName = name; }
            return name;
        }
        catch (Exception ex)
        {
            Log.Warn("GameDetect", ex.Message);
            return "Desktop";
        }
    }

    /// <summary>
    /// Разрешение имени без кэша, самая дорогая часть. null: программа неизвестна
    /// и её окно на игру не похоже.
    /// </summary>
    private static string? Resolve(Process proc, string exe, IntPtr hwnd)
    {
        var database = _database;

        // Словарь игр проверяем первым: если имя вдруг попадёт и в список
        // исключений, игра всё равно выиграет.
        if (database.TryGetGame(exe, out var known)) return Sanitize(known);
        if (exe.Equals(SelfProcess, StringComparison.OrdinalIgnoreCase)) return "Desktop";
        if (database.IsIgnored(exe)) return "Desktop";

        string? path = null;
        try { path = proc.MainModule?.FileName; }
        catch { /* MainModule недоступен у процессов с более высокими правами */ }

        // Всё из системных папок Windows — точно не игра (игры туда не ставятся)
        if (IsWindowsComponent(path)) return "Desktop";

        // Незнакомая программа считается игрой, только если она из папки игр
        // (Steam, Epic, Xbox и другие) или её окно закрывает весь монитор. Иначе
        // любое окно на переднем плане, от оверлея до утилиты, заводило себе
        // папку в библиотеке записей.
        if (!IsGameLibraryPath(path) && !CoversMonitor(hwnd)) return null;

        // У Steam самое точное имя игры: папка в steamapps\common
        if (SteamFolderName(path) is { } steamName) return Sanitize(steamName);

        // Пробуем человекочитаемое имя из ресурсов exe
        if (path is not null)
            try
            {
                string? desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 60)
                    return Sanitize(desc.Trim());
            }
            catch { }

        return Sanitize(exe);
    }

    /// <summary>Куда ставят игры магазины и лаунчеры.</summary>
    private static readonly string[] GameLibraryMarkers =
    [
        @"\steamapps\common\", @"\Epic Games\", @"\Riot Games\", @"\XboxGames\",
        @"\GOG Games\", @"\GOG Galaxy\Games\", @"\Ubisoft Game Launcher\games\",
        @"\EA Games\", @"\Rockstar Games\", @"\Wargaming.net\", @"\Lesta\",
        @"\VK Play\", @"\itch\apps\", @"\Battle.net\", @"\WindowsApps\",
        @"\Games\", @"\Игры\",
    ];

    internal static bool IsGameLibraryPath(string? path) =>
        !string.IsNullOrEmpty(path) &&
        GameLibraryMarkers.Any(marker => path.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>«…\steamapps\common\Dead by Daylight\…» даёт «Dead by Daylight».</summary>
    internal static string? SteamFolderName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        const string marker = @"\steamapps\common\";
        int at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        string rest = path[(at + marker.Length)..];
        int slash = rest.IndexOf('\\');
        return slash > 0 ? rest[..slash] : null;
    }

    /// <summary>Окно закрывает весь свой монитор: полноэкранная или безрамочная игра.</summary>
    private static bool CoversMonitor(IntPtr hwnd)
    {
        try
        {
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                    out var window, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) != 0)
                return false;
            var monitor = NativeMethods.MonitorFromWindow(hwnd, 2);
            var info = new NativeMethods.MONITORINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
            };
            if (!NativeMethods.GetMonitorInfoW(monitor, ref info)) return false;
            var m = info.rcMonitor;
            return window.Left <= m.Left + 2 && window.Top <= m.Top + 2 &&
                   window.Right >= m.Right - 2 && window.Bottom >= m.Bottom - 2;
        }
        catch { return false; }
    }

    /// <summary>Программа лежит в системных папках Windows — служебное окно, а не игра.</summary>
    private static bool IsWindowsComponent(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return path.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Имя папки без запрещённых символов.</summary>
    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(name) ? "Desktop" : name;
    }
}
