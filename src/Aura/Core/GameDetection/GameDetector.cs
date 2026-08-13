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

            string name = Resolve(proc, exe);

            lock (_cacheSync) { _cachedPid = pid; _cachedExe = exe; _cachedName = name; }
            return name;
        }
        catch (Exception ex)
        {
            Log.Warn("GameDetect", ex.Message);
            return "Desktop";
        }
    }

    /// <summary>Разрешение имени без кэша — самая дорогая часть.</summary>
    private static string Resolve(Process proc, string exe)
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
