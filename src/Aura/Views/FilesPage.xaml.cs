using System.Windows;
using System.Windows.Media.Animation;
using Aura.Core.Storage;

namespace Aura.Views;

/// <summary>Папка записей, сколько занято и когда чистить старое.</summary>
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
        AutoDelete.IsChecked = s.AutoDeleteOldClips;
        MaxFolder.Text = s.MaxFolderSizeGb.ToString();
        MinFree.Text = s.MinFreeSpaceGb.ToString();
        UpdateLimitRows();
        _loading = false;
    }

    private void ShowStats(StorageStats stats)
    {
        var s = Services.Settings.Current;
        CountText.Text = stats.ClipCount.ToString();
        UsedText.Text = ByteSize.Format(stats.FolderBytes);
        FreeText.Text = ByteSize.Format(stats.FreeDiskBytes);

        // Полоса лимита имеет смысл только при включённой автоочистке:
        // без неё никакого потолка нет и показывать «занято N%» не от чего.
        long limit = s.MaxFolderSizeGb * 1024L * 1024 * 1024;
        bool hasLimit = s.AutoDeleteOldClips && limit > 0;
        UsageBar.Visibility = hasLimit ? Visibility.Visible : Visibility.Collapsed;

        if (hasLimit)
        {
            double part = Math.Min(1, stats.FolderBytes / (double)limit);
            UsedFill.BeginAnimation(WidthProperty,
                new DoubleAnimation(((FrameworkElement)UsedFill.Parent).ActualWidth * part, TimeSpan.FromSeconds(0.7))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            UsageText.Text = $"Занято {part * 100:0}% от лимита в {s.MaxFolderSizeGb} ГБ. " +
                             "Когда упрётся, самые старые записи удалятся сами.";
        }
        else
        {
            UsageText.Text = "Записи копятся, пока есть место на диске. Ничего не удаляется само.";
        }
    }

    private void UpdateLimitRows()
    {
        var visible = AutoDelete.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LimitRow.Visibility = LimitSeparator.Visibility = visible;
        FreeRow.Visibility = FreeSeparator.Visibility = visible;
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Settings.Current.GroupByGame = GroupByGame.IsChecked == true;
        Services.Settings.Save("storage");
    }

    private void AutoDelete_Changed(object sender, RoutedEventArgs e)
    {
        UpdateLimitRows();
        if (_loading) return;
        Services.Settings.Current.AutoDeleteOldClips = AutoDelete.IsChecked == true;
        Services.Settings.Save("storage");
        ShowStats(Services.Storage.GetStats());
    }

    private void Number_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = Services.Settings.Current;
        if (int.TryParse(MaxFolder.Text, out int max)) s.MaxFolderSizeGb = Math.Clamp(max, 0, 5000);
        if (int.TryParse(MinFree.Text, out int free)) s.MinFreeSpaceGb = Math.Clamp(free, 1, 500);
        MaxFolder.Text = s.MaxFolderSizeGb.ToString();
        MinFree.Text = s.MinFreeSpaceGb.ToString();
        Services.Settings.Save("storage");
        ShowStats(Services.Storage.GetStats());
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => App.OpenRecordingsFolder();

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
