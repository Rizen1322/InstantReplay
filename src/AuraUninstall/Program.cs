using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace AuraUninstall;

internal static class Program
{
    private const string AppName = "Aura";
    private const string OldAppName = "Instant Replay";
    private const string ProcessName = "Aura";
    private const string OldProcessName = "InstantReplay";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Aura";
    private const string OldUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\InstantReplay";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string IdentityPackageName = "Rizen1322.Aura";
    private const string InstallMarkerName = ".aura-install-root";

    private const uint MbOk = 0;
    private const uint MbOkCancel = 1;
    private const uint MbIconInformation = 0x40;
    private const uint MbIconWarning = 0x30;
    private const int IdOk = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);

    [STAThread]
    private static int Main(string[] args)
    {
        if (!UninstallLayout.TryResolveInstallRoot(Environment.ProcessPath, out string root) ||
            !File.Exists(Path.Combine(root, InstallMarkerName)))
        {
            MessageBoxW(0, "Не удалось определить безопасную папку установки.", "Aura", MbOk | MbIconWarning);
            return 2;
        }

        bool silent = args.Any(arg => arg.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
                                      arg.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        if (!silent && MessageBoxW(
                0,
                "Удалить Aura?\n\nЗаписи и настройки останутся на месте.",
                "Удаление Aura",
                MbOkCancel | MbIconWarning) != IdOk)
            return 1;

        try
        {
            StopProcesses();
            DeleteShortcuts();
            DeleteRegistration();
            DeleteScheduledTasks();
            UnregisterIdentityPackage();
            ScheduleDirectoryRemoval(root);

            if (!silent)
                MessageBoxW(0, "Aura удалена. Записи и настройки сохранены.", "Aura", MbOk | MbIconInformation);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBoxW(0, $"Не удалось удалить Aura:\n\n{ex.Message}", "Aura", MbOk | MbIconWarning);
            return 3;
        }
    }

    private static void StopProcesses()
    {
        int current = Environment.ProcessId;
        foreach (string name in new[] { ProcessName, OldProcessName })
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.Id == current) continue;
                    try { process.Kill(); process.WaitForExit(3000); } catch { }
                }
            }
    }

    private static void DeleteShortcuts()
    {
        foreach (string name in new[] { AppName, OldAppName })
        {
            TryDelete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs",
                $"{name}.lnk"));
            TryDelete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"{name}.lnk"));
        }
    }

    private static void DeleteRegistration()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(OldUninstallKey, throwOnMissingSubKey: false); } catch { }
        try
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue(ProcessName, throwOnMissingValue: false);
            run?.DeleteValue(OldProcessName, throwOnMissingValue: false);
        }
        catch { }
    }

    private static void DeleteScheduledTasks()
    {
        foreach (string task in new[] { AppName, OldProcessName })
            Run("schtasks.exe", $"/Delete /TN \"{task}\" /F", 5000);
    }

    private static void UnregisterIdentityPackage()
    {
        Run(
            "powershell.exe",
            $"-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass " +
            $"-Command \"Get-AppxPackage -Name '{IdentityPackageName}' | Remove-AppxPackage -ErrorAction SilentlyContinue\"",
            60000);
    }

    private static void ScheduleDirectoryRemoval(string root)
    {
        string escaped = root.Replace("'", "''", StringComparison.Ordinal);
        string script = $"Start-Sleep -Seconds 2; Remove-Item -LiteralPath '{escaped}' -Recurse -Force -ErrorAction SilentlyContinue";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        Process.Start(new ProcessStartInfo(
            "powershell.exe",
            $"-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static void Run(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null) return;
            if (!process.WaitForExit(timeoutMs)) process.Kill();
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
