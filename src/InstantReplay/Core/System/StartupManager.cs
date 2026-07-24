using System.Diagnostics;
using Microsoft.Win32;
using InstantReplay.Core.Logging;

namespace InstantReplay.Core.SystemIntegration;

/// <summary>
/// Автозапуск вместе с Windows.
///
/// Приложение работает от администратора (нужно для хоткеев поверх игр с анти-читом),
/// а такой процесс НЕЛЬЗЯ запустить из HKCU\...\Run — Windows молча его пропускает
/// при входе. Поэтому автозапуск делаем через Планировщик задач с наивысшими правами
/// (RunLevel=Highest): он стартует при входе в систему БЕЗ запроса UAC.
///
/// Заодно подчищаем старую запись в Run (осталась от прежних версий) — иначе была бы
/// вторая, неработающая попытка автозапуска.
/// </summary>
public static class StartupManager
{
    private const string TaskName = "InstantReplay";
    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string ExePath => Environment.ProcessPath ?? "";

    public static void SetEnabled(bool enabled)
    {
        RemoveLegacyRunEntry();
        if (enabled) CreateTask();
        else DeleteTask();
    }

    /// <summary>Есть ли задача автозапуска на ТЕКУЩИЙ путь exe.</summary>
    public static bool IsEnabled()
    {
        try
        {
            var (code, _) = RunSchtasks(capture: true, "/Query", "/TN", TaskName);
            return code == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Сверка при старте: восстановить пропавшую задачу и обновить устаревший путь
    /// (после переустановки/переноса папки).
    /// </summary>
    public static void Reconcile(bool wanted)
    {
        try
        {
            RemoveLegacyRunEntry();
            var (code, output) = RunSchtasks(capture: true, "/Query", "/TN", TaskName, "/FO", "LIST", "/V");
            bool exists = code == 0;
            bool pathOk = exists && output.Contains(ExePath, StringComparison.OrdinalIgnoreCase);

            if (wanted && !pathOk)
            {
                Log.Warn("Startup", exists ? "Путь автозапуска устарел — обновляю" : "Задача автозапуска отсутствовала — создаю");
                CreateTask();
            }
            else if (!wanted && exists)
            {
                DeleteTask();
            }
        }
        catch (Exception ex) { Log.Warn("Startup", $"Сверка автозапуска: {ex.Message}"); }
    }

    private static void CreateTask()
    {
        // /RL HIGHEST — запуск с наивысшими правами (без UAC при входе);
        // /SC ONLOGON — при входе текущего пользователя; /F — перезаписать.
        // /TR со вложенным путём: сам путь в кавычках, чтобы работали пробелы.
        string tr = $"\"{ExePath}\" --minimized";
        var (code, output) = RunSchtasks(capture: true,
            "/Create", "/TN", TaskName, "/TR", tr, "/SC", "ONLOGON", "/RL", "HIGHEST", "/F");
        if (code == 0) Log.Info("Startup", $"Автозапуск включён (Планировщик задач): {ExePath}");
        else Log.Error("Startup", $"Не удалось создать задачу автозапуска (код {code}): {output.Trim()}");
    }

    private static void DeleteTask()
    {
        var (code, _) = RunSchtasks(capture: false, "/Delete", "/TN", TaskName, "/F");
        Log.Info("Startup", code == 0 ? "Автозапуск выключен" : "Задача автозапуска и так отсутствовала");
    }

    private static void RemoveLegacyRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
            if (key?.GetValue(TaskName) is not null)
            {
                key.DeleteValue(TaskName, throwOnMissingValue: false);
                Log.Info("Startup", "Убрана устаревшая запись автозапуска из HKCU\\Run");
            }
        }
        catch { }
    }

    private static (int Code, string Output) RunSchtasks(bool capture, params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture
        };
        foreach (var a in args) psi.ArgumentList.Add(a); // корректное экранирование за нас
        using var p = Process.Start(psi)!;
        string output = "";
        if (capture)
            output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(8000);
        return (p.HasExited ? p.ExitCode : -1, output);
    }
}
