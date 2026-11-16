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
        // Права администратора просим сами, а не манифестом (см. app.manifest)
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            if (!new System.Security.Principal.WindowsPrincipal(identity)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                try
                {
                    var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                    foreach (string arg in args) psi.ArgumentList.Add(arg);
                    Process.Start(psi);
                    return 0;
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    return 1;   // отказ в окне UAC
                }
            }

        if (!UninstallLayout.TryResolveInstallRoot(Environment.ProcessPath, out string root) ||
            !UninstallLayout.IsSafeInstallRoot(root, Environment.ProcessPath))
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
        bool asked = false;
        try
        {
            if (EventWaitHandle.TryOpenExisting(@"Global\Aura.Quit", out var quit))
                using (quit) asked = quit.Set();
        }
        catch { }

        int current = Environment.ProcessId;
        foreach (string name in new[] { ProcessName, OldProcessName, "Aura.EncoderHost" })
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.Id == current) continue;
                    try
                    {
                        if (!process.WaitForExit(asked ? 5000 : 0))
                        {
                            process.Kill();
                            process.WaitForExit(3000);
                        }
                    }
                    catch { }
                }
            }
    }

    private static void DeleteShortcuts()
    {
        foreach (string name in new[] { AppName, OldAppName })
        {
            TryDelete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                "Programs",
                $"{name}.lnk"));
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
        try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }
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
        string fullRoot = Path.GetFullPath(root);
        string appDir = Path.Combine(fullRoot, "app");
        string marker = Path.Combine(fullRoot, InstallMarkerName);
        string legacyUninstaller = Path.Combine(fullRoot, "Uninstall.exe");

        TryDelete(legacyUninstaller);
        TryDelete(marker);

        string escapedRoot = fullRoot.Replace("'", "''", StringComparison.Ordinal);
        string escapedApp = appDir.Replace("'", "''", StringComparison.Ordinal);
        string escapedMarker = marker.Replace("'", "''", StringComparison.Ordinal);
        string escapedLegacy = legacyUninstaller.Replace("'", "''", StringComparison.Ordinal);
        string script =
            "Start-Sleep -Seconds 2; " +
            $"$root = '{escapedRoot}'; $app = '{escapedApp}'; " +
            "function Test-NoReparse([string]$p) { " +
            "  if (-not (Test-Path -LiteralPath $p)) { return $true } " +
            "  $item = Get-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue; " +
            "  return ($null -ne $item) -and (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) " +
            "}; " +
            "if ((Test-NoReparse $root) -and (Test-NoReparse $app)) { " +
            "  if (Test-Path -LiteralPath $app) { Remove-Item -LiteralPath $app -Recurse -Force -ErrorAction SilentlyContinue }; " +
            $"  Remove-Item -LiteralPath '{escapedMarker}' -Force -ErrorAction SilentlyContinue; " +
            $"  Remove-Item -LiteralPath '{escapedLegacy}' -Force -ErrorAction SilentlyContinue; " +
            "  if ((Test-Path -LiteralPath $root) -and ((Get-ChildItem -LiteralPath $root -Force -ErrorAction SilentlyContinue | Measure-Object).Count -eq 0)) { " +
            "    Remove-Item -LiteralPath $root -Force -ErrorAction SilentlyContinue " +
            "  } " +
            "}";
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
