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

    protected override void OnStartup(StartupEventArgs e)
    {
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

        base.OnStartup(e);
    }
}
