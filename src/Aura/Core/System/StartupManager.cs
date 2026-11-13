using System.Runtime.Versioning;
using System.Xml.Linq;
using System.Runtime.InteropServices;
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

    [SupportedOSPlatform("windows")]
    public static void SetEnabled(bool enabled)
    {
        try
        {
            RemoveLegacyRunEntry();
            if (enabled) CreateTask();
            else DeleteTask();
        }
        catch (Exception ex) { Log.Warn("Startup", $"Автозапуск: {ex.Message}"); }
    }

    /// <summary>Есть ли задача автозапуска на ТЕКУЩИЙ путь exe.</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsEnabled()
    {
        try
        {
            return TaskMatches(ReadTaskXml(), ExePath);
        }
        catch { return false; }
    }

    /// <summary>
    /// Сверка при старте: восстановить пропавшую задачу и обновить устаревший путь
    /// (после переустановки/переноса папки).
    /// </summary>
    /// <returns>
    /// true — найдена задача прежнего вида, которая всегда запускала с --minimized.
    /// Вызывающий включает «Стартовать свёрнутым в трей», чтобы после обновления
    /// Aura при входе в Windows вела себя как раньше, а переключатель говорил правду.
    /// </returns>
    [SupportedOSPlatform("windows")]
    public static bool Reconcile(bool wanted)
    {
        bool legacyMinimized = false;
        try
        {
            HardenInstallDirectory();
            RemoveLegacyRunEntry();
            string output = ReadTaskXml();
            bool exists = output.Length > 0;
            bool pathOk = TaskMatches(output, ExePath);
            // Старые задачи созданы без приоритета: Планировщик ставит 7 (ниже обычного)
            bool priorityOk = exists && output.Contains("<Priority>4</Priority>", StringComparison.Ordinal);

            // Старые задачи всегда запускали с --minimized, и переключатель «Стартовать
            // свёрнутым в трей» при автозапуске ничего не решал
            legacyMinimized = exists && output.Contains("--minimized", StringComparison.Ordinal);
            bool argsOk = exists && output.Contains("--autostart", StringComparison.Ordinal);

            if (wanted && (!pathOk || !priorityOk || !argsOk))
            {
                Log.Warn("Startup", !exists ? "Задача автозапуска отсутствовала — создаю"
                                   : !pathOk ? "Путь автозапуска устарел — обновляю"
                                   : !priorityOk ? "Приоритет автозапуска ниже обычного — обновляю"
                                   : "Задача автозапуска прежнего вида — обновляю");
                CreateTask();
            }
            else if (!wanted && exists)
            {
                DeleteTask();
            }
        }
        catch (Exception ex) { Log.Warn("Startup", $"Сверка автозапуска: {ex.Message}"); }
        return legacyMinimized;
    }

    /// <summary>
    /// Папка установки, куда может писать обычный пользователь, превращает задачу
    /// с наивысшими правами в дыру: любая программа подменит Aura.exe или подложит
    /// рядом DLL и получит права администратора при входе, без запроса UAC.
    /// Установщик теперь ставит в Program Files и сам закрывает права, а здесь
    /// вторая линия: старые установки закрываются при первом же запуске.
    /// Трогаем только настоящую раскладку установки: &lt;корень&gt;\app\Aura.exe и метка.
    /// </summary>
    [SupportedOSPlatform("windows")]
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
            security.SetOwner(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null));
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
    [SupportedOSPlatform("windows")]
    private static bool WritableByUsers(string directory)
    {
        var acl = new DirectoryInfo(directory).GetAccessControl();
        string owner = acl.GetOwner(typeof(System.Security.Principal.SecurityIdentifier))!.Value;
        if (owner is not ("S-1-5-18" or "S-1-5-32-544" or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"))
            return true;
        var rules = acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
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

    [SupportedOSPlatform("windows")]
    private static void CreateTask()
    {
#if DEBUG
        Log.Warn("Startup", "Автозапуск отладочной сборки не создаётся; включите его в установленной Aura");
#else
        // /RL HIGHEST — запуск с наивысшими правами (без UAC при входе);
        // /SC ONLOGON — при входе текущего пользователя; /F — перезаписать.
        // /TR со вложенным путём: сам путь в кавычках, чтобы работали пробелы.
        // Через XML: только так задаются приоритет и лимиты. По умолчанию Планировщик
        // запускает с приоритетом 7 (ниже обычного), останавливает задачу через 72 часа
        // и не стартует от батареи. Aura в классе ниже обычного под игрой теряла кадры.
        if (!Tools.ExecutableTrust.IsProtected(ExePath))
            throw new InvalidOperationException("Автозапуск с повышенными правами разрешён только из защищённой папки установки.");
        string user = TaskUserSid();
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{System.Security.SecurityElement.Escape(user)}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author">
                <UserId>{System.Security.SecurityElement.Escape(user)}</UserId>
                <LogonType>InteractiveToken</LogonType>
                <RunLevel>HighestAvailable</RunLevel>
              </Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec>
                <Command>"{System.Security.SecurityElement.Escape(ExePath)}"</Command>
                <Arguments>--autostart</Arguments>
                <WorkingDirectory>{System.Security.SecurityElement.Escape(Path.GetDirectoryName(ExePath))}</WorkingDirectory>
              </Exec></Actions>
            </Task>
            """;
        WithTaskFolder(folder =>
        {
            object task = folder.RegisterTask(TaskName, xml, 6, null, null, 3, null);
            Marshal.FinalReleaseComObject(task);
            return true;
        });
        Log.Info("Startup", $"Автозапуск включён (Планировщик задач): {ExePath}");
#endif
    }

    [SupportedOSPlatform("windows")]
    private static string TaskUserSid()
    {
        using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
        IntPtr shell = Interop.NativeMethods.GetShellWindow();
        if (shell == IntPtr.Zero) throw new InvalidOperationException("Оболочка Windows пока недоступна.");
        Interop.NativeMethods.GetWindowThreadProcessId(shell, out uint pid);
        using var process = System.Diagnostics.Process.GetProcessById((int)pid);
        if (!Interop.NativeMethods.OpenProcessToken(process.Handle, 8, out var token))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var interactive = new System.Security.Principal.WindowsIdentity(token.DangerousGetHandle()))
        {
            if (current.User != interactive.User)
                throw new InvalidOperationException("Автозапуск нельзя настроить от имени другого администратора. Запустите Aura под учётной записью пользователя Windows.");
        }
        return current.User!.Value;
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteTask()
    {
        if (ReadTaskXml().Length == 0) return;
        WithTaskFolder(folder => { folder.DeleteTask(TaskName, 0); return true; });
        Log.Info("Startup", "Автозапуск выключен");
    }

    [SupportedOSPlatform("windows")]
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

    // COM avoids writable task files and console encoding/pipe timeouts.
    [SupportedOSPlatform("windows")]
    private static T WithTaskFolder<T>(Func<dynamic, T> action)
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        object? folder = null;
        try
        {
            service.Connect();
            folder = service.GetFolder(@"\");
            return action(folder);
        }
        finally
        {
            if (folder is not null) Marshal.FinalReleaseComObject(folder);
            Marshal.FinalReleaseComObject(service);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string ReadTaskXml() => WithTaskFolder(folder =>
    {
        object? task = null;
        try { task = folder.GetTask(TaskName); return (string)((dynamic)task).Xml; }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { return ""; }
        finally { if (task is not null) Marshal.FinalReleaseComObject(task); }
    });

    internal static bool TaskMatches(string xml, string exePath)
    {
        if (xml.Length == 0) return false;
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var exec = XDocument.Parse(xml).Root?.Element(ns + "Actions")?.Element(ns + "Exec");
        return string.Equals(exec?.Element(ns + "Command")?.Value.Trim('"'), exePath,
                             StringComparison.OrdinalIgnoreCase);
    }
}
