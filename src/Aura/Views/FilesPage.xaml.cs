using System.Windows;
using Aura.Core.Storage;

namespace Aura.Views;

/// <summary>
/// Папка записей и сколько в ней занято.
///
/// Автоочистки по лимиту больше нет: приложение не должно молча удалять чужие
/// записи, а «лимит папки» ничего не гарантировал — место кончалось от чего угодно
/// ещё. Разбирать, что оставить, человек решает сам в «Клипах».
/// </summary>
public partial class FilesPage : PageBase
{
    private bool _loading;

    public override string Title => "Файлы";

    public FilesPage()
    {
        InitializeComponent();
        Services.Storage.StatsChanged += stats => Dispatcher.BeginInvoke(() => ShowStats(stats));
        Loaded += (_, _) => Load();
    }

    public override void OnShown()
    {
        Load();
        ShowStats(Services.Storage.GetStats());
    }

    private void Load()
    {
        _loading = true;
        var s = Services.Settings.Current;
        PathText.Text = s.SaveRootPath;
        GroupSub.Text = Path.Combine(s.SaveRootPath, "Counter-Strike 2") + @"\…";
        GroupByGame.IsChecked = s.GroupByGame;
        ShowTrimTool();
        _loading = false;
    }

    private void ShowStats(StorageStats stats)
    {
        CountText.Text = stats.ClipCount.ToString();
        UsedText.Text = ByteSize.Format(stats.FolderBytes);
        FreeText.Text = ByteSize.Format(stats.FreeDiskBytes);
        UsageText.Text = "Записи копятся, пока есть место на диске. Само ничего не удаляется — " +
                         "лишнее убирается вручную в «Клипах».";
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Settings.Current.GroupByGame = GroupByGame.IsChecked == true;
        Services.Settings.Save("storage");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => App.OpenRecordingsFolder();

    // ---------------- Программа для обрезки ----------------

    /// <summary>
    /// Пункт «Обрезать» в меню клипа открывает файл в LosslessCut. Путь ищется
    /// автоматически при первом обращении, здесь его можно посмотреть и сменить.
    /// </summary>
    private void ShowTrimTool()
    {
        string? path = Services.Settings.Current.LosslessCutPath;
        bool known = !string.IsNullOrWhiteSpace(path) && File.Exists(path);

        TrimToolPath.Text = known
            ? path!
            : ClipCommands.FindLosslessCut() is { } found
                ? found + "  (найден автоматически)"
                : "не выбран — спросим при первой обрезке";
        TrimToolClear.IsEnabled = known;
    }

    private void TrimTool_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "LosslessCut.exe",
            Filter = "LosslessCut|LosslessCut.exe|Программы (*.exe)|*.exe"
        };
        if (dialog.ShowDialog() != true) return;

        Services.Settings.Current.LosslessCutPath = dialog.FileName;
        Services.Settings.Save("tools");
        ShowTrimTool();
    }

    private void TrimToolClear_Click(object sender, RoutedEventArgs e)
    {
        Services.Settings.Current.LosslessCutPath = null;
        Services.Settings.Save("tools");
        ShowTrimTool();
    }

    /// <summary>
    /// Выбор папки системным диалогом. OpenFolderDialog из WPF (.NET 8+) —
    /// без ссылки на WinForms и без COM-обёрток.
    /// </summary>
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Куда сохранять записи",
            InitialDirectory = Services.Settings.Current.SaveRootPath
        };
        if (dialog.ShowDialog() != true) return;

        Services.Settings.Current.SaveRootPath = dialog.FolderName;
        Directory.CreateDirectory(dialog.FolderName);
        Services.Settings.Save("storage");
        Load();
        ShowStats(Services.Storage.GetStats());
    }
}
