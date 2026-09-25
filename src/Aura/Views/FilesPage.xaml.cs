using System.Windows;
using System.Windows.Controls;
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
        AddTips(Aside, "files");
        HideAsideWhenNarrow(AsideCol, Aside);
        Services.Storage.StatsChanged += stats => Dispatcher.BeginInvoke(() => ShowStats(stats));
        Loaded += (_, _) => Load();
    }

    public override bool FillsWindow => true;

    public override void OnShown()
    {
        Load();
        ShowStats(Services.Storage.GetStats());
        _ = LoadTopGamesAsync();
    }

    private void Load()
    {
        _loading = true;
        var s = Services.Settings.Current;
        PathText.Text = s.SaveRootPath;
        ShotsPathText.Text = s.ScreenshotFolder;
        GroupSub.Text = "Отдельная папка на каждую игру: " + Path.Combine(s.SaveRootPath, "Counter-Strike 2");
        GroupByGame.IsChecked = s.GroupByGame;
        LimitBox.Text = s.AttachmentSizeMb.ToString();
        _loading = false;
    }

    private void ShowStats(StorageStats stats)
    {
        CountText.Text = stats.ClipCount.ToString();
        UsedText.Text = ByteSize.Format(stats.FolderBytes);
        FreeText.Text = ByteSize.Format(stats.FreeDiskBytes);
        // Доля записей от всего, что им доступно: занятое записями плюс свободное
        double total = stats.FolderBytes + stats.FreeDiskBytes;
        double part = total > 0 ? stats.FolderBytes / total : 0;
        Dispatcher.BeginInvoke(() =>
        {
            double full = ((FrameworkElement)UsedBar.Parent).ActualWidth;
            UsedBar.Width = Math.Max(part > 0 ? 6 : 0, full * part);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Какие игры занимают больше всего: три строки в боковой панели.</summary>
    private async Task LoadTopGamesAsync()
    {
        var s = Services.Settings.Current;
        string root = s.SaveRootPath;
        List<(string Game, long Bytes)> top;
        try
        {
            top = await Task.Run(() => Core.Library.ClipLibrary.Scan(root)
                .GroupBy(i => i.Game)
                .Select(g => (g.Key, g.Sum(i => i.SizeBytes)))
                .OrderByDescending(g => g.Item2)
                .Take(4)
                .ToList());
        }
        catch { return; }

        TopGames.Children.Clear();
        TopGames.RowDefinitions.Clear();
        for (int i = 0; i < top.Count; i++)
        {
            TopGames.RowDefinitions.Add(new RowDefinition());
            var name = new TextBlock { Text = top[i].Game, Style = (Style)FindResource("KvKey"), TextTrimming = TextTrimming.CharacterEllipsis };
            var size = new TextBlock { Text = ByteSize.Format(top[i].Bytes), Style = (Style)FindResource("KvVal") };
            Grid.SetRow(name, i);
            Grid.SetRow(size, i);
            Grid.SetColumn(size, 1);
            TopGames.Children.Add(name);
            TopGames.Children.Add(size);
        }
        TopGamesPanel.Visibility = top.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Digits_Only(object sender, System.Windows.Input.TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsDigit);

    private void Limit_Key(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) Limit_Commit(sender, e);
    }

    /// <summary>Своё число для сжатия под чат: сохраняется сразу, границы 2–2048 МБ.</summary>
    private void Limit_Commit(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        int value = int.TryParse(LimitBox.Text, out int v) ? Math.Clamp(v, 2, 2048) : Services.Settings.Current.AttachmentSizeMb;
        LimitBox.Text = value.ToString();
        if (value != Services.Settings.Current.AttachmentSizeMb)
            Services.Settings.Update(s => s.AttachmentSizeMb = value, "storage");
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Settings.Update(s => s.GroupByGame = GroupByGame.IsChecked == true, "storage");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => App.OpenRecordingsFolder();

    private void OpenShotsFolder_Click(object sender, RoutedEventArgs e) => App.OpenScreenshotFolder();

    /// <summary>
    /// Папка скриншотов. Смена трогает и «Клипы»: список собирается из обеих папок,
    /// поэтому событие то же самое — storage.
    /// </summary>
    private void BrowseShots_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Куда сохранять скриншоты",
            InitialDirectory = Services.Settings.Current.ScreenshotFolder
        };
        if (dialog.ShowDialog() != true) return;

        string folder = AuraFolderIn(dialog.FolderName);
        Directory.CreateDirectory(folder);
        Services.Settings.Update(s => s.ScreenshotFolder = folder, "storage");
        Load();
        ClipCommands.NotifyLibraryChanged();
    }

    /// <summary>
    /// Своя папка внутри выбранной: человек указывает «Документы», а файлы должны
    /// лечь в «Документы\Aura» — сваливать их прямо в пользовательскую папку нельзя,
    /// её потом не разгрести. Если выбрана уже сама Aura, второй такой же
    /// вложенности не делаем.
    /// </summary>
    private static string AuraFolderIn(string chosen)
    {
        try
        {
            string name = new DirectoryInfo(chosen).Name;
            if (name.Equals("Aura", StringComparison.OrdinalIgnoreCase)) return chosen;
        }
        catch { /* нераспознаваемый путь — просто добавим подпапку */ }
        return Path.Combine(chosen, "Aura");
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

        string folder = AuraFolderIn(dialog.FolderName);
        Directory.CreateDirectory(folder);
        Services.Settings.Update(s => s.SaveRootPath = folder, "storage");
        Load();
        ShowStats(Services.Storage.GetStats());
    }
}
