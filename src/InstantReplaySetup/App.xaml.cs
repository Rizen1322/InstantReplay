using System.Windows;

namespace InstantReplaySetup;

public partial class App : Application
{
    public static bool UninstallMode { get; private set; }

    /// <summary>
    /// Тихое обновление: запускается самим приложением («/update &lt;папка&gt;»).
    /// Без вопросов ставит новую версию в ТУ ЖЕ папку и перезапускает приложение —
    /// пользователь видит только окно прогресса.
    /// </summary>
    public static bool UpdateMode { get; private set; }

    /// <summary>Куда ставить в режиме обновления (корень установки, не подпапка app).</summary>
    public static string? UpdateTarget { get; private set; }

    /// <summary>
    /// «--snapshot файл.png»: нарисовать окно в картинку за пределами экрана и
    /// выйти, ничего не устанавливая и без музыки. Для проверки вёрстки.
    /// </summary>
    public static string? SnapshotPath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Снимок вёрстки ничего не ставит, права ему не нужны
        bool snapshot = e.Args.Any(a => a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase));
        if (!snapshot && !EnsureElevated(e.Args))
        {
            Shutdown();
            return;
        }

        UninstallMode = e.Args.Any(a =>
            a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));

        for (int i = 0; i < e.Args.Length; i++)
        {
            if (!e.Args[i].Equals("/update", StringComparison.OrdinalIgnoreCase) &&
                !e.Args[i].Equals("--update", StringComparison.OrdinalIgnoreCase)) continue;

            UpdateMode = true;
            // Следом идёт путь установки; если его нет — MainWindow возьмёт путь по умолчанию
            if (i + 1 < e.Args.Length && !e.Args[i + 1].StartsWith('/'))
                UpdateTarget = e.Args[i + 1];
            break;
        }

        int snap = Array.FindIndex(e.Args, a => a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase));
        if (snap >= 0 && snap + 1 < e.Args.Length) SnapshotPath = e.Args[snap + 1];

        base.OnStartup(e);
    }

    /// <summary>
    /// Установке нужны права администратора (Program Files, общая запись в реестре,
    /// права на папку). Манифест их не требует (см. app.manifest), поэтому просим
    /// здесь: без прав перезапускаемся с запросом UAC и выходим. Из Aura при
    /// обновлении установщик запускается уже с её правами, и запроса нет.
    /// false — текущий процесс должен завершиться.
    /// </summary>
    private static bool EnsureElevated(string[] args)
    {
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            if (new System.Security.Principal.WindowsPrincipal(identity)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                return true;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            System.Diagnostics.Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Человек отказался в окне UAC: ставить без прав нечего
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось запросить права администратора:\n{ex.Message}", "Aura",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return false;
    }
}

