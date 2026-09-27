using System.Diagnostics;
using Microsoft.Win32;
using Aura.Core.Logging;

namespace Aura.Core.SystemIntegration;

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
    private const string TaskName = "Aura";
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
            HardenInstallDirectory();
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

    /// <summary>
    /// Папка установки, куда может писать обычный пользователь, превращает задачу
    /// с наивысшими правами в дыру: любая программа подменит Aura.exe или подложит
    /// рядом DLL и получит права администратора при входе, без запроса UAC.
    /// Установщик теперь ставит в Program Files и сам закрывает права, а здесь
    /// вторая линия: старые установки закрываются при первом же запуске.
    /// Трогаем только настоящую раскладку установки: &lt;корень&gt;\app\Aura.exe и метка.
    /// </summary>
    private static void HardenInstallDirectory()
    {
        try
        {
            string app = Path.GetDirectoryName(ExePath)!;
            string? root = Path.GetDirectoryName(app);
            if (root is null || !Path.GetFileName(app).Equals("app", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(Path.Combine(root, ".aura-install-root"))) return;
            if (!WritableByUsers(root)) return;

            var security = new System.Security.AccessControl.DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inherit = System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                          System.Security.AccessControl.InheritanceFlags.ObjectInherit;
            void Add(string sid, System.Security.AccessControl.FileSystemRights rights) =>
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(sid), rights, inherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
            Add("S-1-5-18", System.Security.AccessControl.FileSystemRights.FullControl);        // SYSTEM
            Add("S-1-5-32-544", System.Security.AccessControl.FileSystemRights.FullControl);    // Администраторы
            Add("S-1-5-32-545", System.Security.AccessControl.FileSystemRights.ReadAndExecute); // Пользователи
            Add("S-1-15-2-1", System.Security.AccessControl.FileSystemRights.ReadAndExecute);   // Пакеты приложений
            new DirectoryInfo(root).SetAccessControl(security);
            Log.Info("Startup", $"Права на папку установки закрыты от записи: {root}");
        }
        catch (Exception ex) { Log.Warn("Startup", $"Права на папку установки: {ex.Message}"); }
    }

    /// <summary>Может ли в папку писать кто-то, кроме администраторов и системы.</summary>
    private static bool WritableByUsers(string directory)
    {
        var rules = new DirectoryInfo(directory).GetAccessControl()
            .GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
        const System.Security.AccessControl.FileSystemRights write =
            System.Security.AccessControl.FileSystemRights.WriteData |
            System.Security.AccessControl.FileSystemRights.AppendData |
            System.Security.AccessControl.FileSystemRights.Delete |
            System.Security.AccessControl.FileSystemRights.ChangePermissions |
            System.Security.AccessControl.FileSystemRights.TakeOwnership;
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != System.Security.AccessControl.AccessControlType.Allow) continue;
            // «Только для наследования» (CREATOR OWNER) к самой папке не относится
            if ((rule.PropagationFlags & System.Security.AccessControl.PropagationFlags.InheritOnly) != 0) continue;
            string sid = rule.IdentityReference.Value;
            if (sid is "S-1-5-18" or "S-1-5-32-544" or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464") continue;
            if ((rule.FileSystemRights & write) != 0) return true;
        }
        return false;
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
