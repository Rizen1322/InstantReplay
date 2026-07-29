using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using InstantReplay.Core.Storage;

namespace InstantReplay.Views;

public sealed partial class StoragePage : Page
{
    private readonly DirtyState _dirty;
    private bool _loading;
    /// <summary>Выбранная в пикере папка до нажатия «Применить».</summary>
    private string? _pendingPath;

    public StoragePage()
    {
        InitializeComponent();
        _dirty = new DirtyState(DirtyText, ApplyBtn, RevertBtn);

        Loaded += (_, _) =>
        {
            LoadFromSettings();
            Services.Storage.StatsChanged += OnStats;
            OnStats(Services.Storage.GetStats());
        };
        Unloaded += (_, _) => Services.Storage.StatsChanged -= OnStats;
    }

    private void LoadFromSettings()
    {
        _loading = true;
        _dirty.Suspended = true;

        var s = Services.Settings.Current;
        _pendingPath = null;
        PathText.Text = s.SaveRootPath;
        GroupToggle.IsOn = s.GroupByGame;
        TemplateBox.Text = s.FileNameTemplate;
        MaxFolderBox.Value = s.MaxFolderSizeGb;
        MinFreeBox.Value = s.MinFreeSpaceGb;
        AutoDeleteToggle.IsOn = s.AutoDeleteOldClips;
        UpdateLimitsVisibility();

        _dirty.Suspended = false;
        _dirty.Clear();
        _loading = false;
    }

    // Любая правка только помечает страницу изменённой — сохраняет кнопка «Применить».
    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        _dirty.Mark();
        UpdateLimitsVisibility();
    }

    /// <summary>Поля лимитов имеют смысл только при включённом автоудалении.</summary>
    private void UpdateLimitsVisibility() =>
        LimitsPanel.Visibility = AutoDeleteToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
    private void Text_Changed(object sender, TextChangedEventArgs e) => _dirty.Mark();
    private void Number_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => _dirty.Mark();

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var s = Services.Settings.Current;
        if (_pendingPath is { Length: > 0 }) s.SaveRootPath = _pendingPath;
        s.GroupByGame = GroupToggle.IsOn;
        if (!string.IsNullOrWhiteSpace(TemplateBox.Text)) s.FileNameTemplate = TemplateBox.Text.Trim();
        s.MaxFolderSizeGb = (int)MaxFolderBox.Value;
        s.MinFreeSpaceGb = (int)MinFreeBox.Value;
        s.AutoDeleteOldClips = AutoDeleteToggle.IsOn;
        Services.Settings.Save("storage"); // StorageManager пересоберёт индекс по новой папке

        _pendingPath = null;
        _dirty.MarkSaved();
    }

    private void Revert_Click(object sender, RoutedEventArgs e) => LoadFromSettings();

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        // WinUI 3 desktop: пикеру нужен HWND главного окна
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(WindowTracker.Main!);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        // Папка применяется той же кнопкой, что и остальные настройки вкладки:
        // раньше она сохранялась мгновенно, а соседние поля ждали «Применить».
        _pendingPath = folder.Path;
        PathText.Text = folder.Path;
        _dirty.Mark();
    }

    private void OnStats(StorageStats stats) => Services.Dispatcher.Enqueue(() =>
    {
        SavedCountText.Text = Services.Settings.Current.TotalReplaysSaved.ToString();
        UsedText.Text = ByteSize.Format(stats.FolderBytes);
        FreeText.Text = ByteSize.Format(stats.FreeDiskBytes);
        // Пока изменения не применены, в строке пути должна оставаться выбранная папка
        if (!_loading && _pendingPath is null) PathText.Text = stats.RootPath;
        UpdateUsageBar(stats);
    });

    /// <summary>
    /// Полоса «сколько из лимита папки занято». Цифры рядом были, но по ним не
    /// видно, близко ли автоочистка — теперь видно с одного взгляда.
    /// </summary>
    private void UpdateUsageBar(StorageStats stats)
    {
        var settings = Services.Settings.Current;
        int limitGb = settings.MaxFolderSizeGb;
        // Полоса «занято из лимита» показывается только когда лимит реально работает,
        // то есть при включённом автоудалении. Иначе это цифра ни о чём.
        if (limitGb <= 0 || !settings.AutoDeleteOldClips)
        {
            UsagePanel.Visibility = Visibility.Collapsed;
            return;
        }
        UsagePanel.Visibility = Visibility.Visible;

        long limitBytes = limitGb * 1024L * 1024 * 1024;
        double percent = Math.Clamp(stats.FolderBytes * 100.0 / limitBytes, 0, 100);
        UsageBar.Value = percent;

        string brush = percent >= 90 ? "SystemFillColorCriticalBrush"
                     : percent >= 70 ? "SystemFillColorCautionBrush"
                                     : "SystemFillColorSuccessBrush";
        UsageBar.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[brush];

        UsageText.Text = $"{ByteSize.Format(stats.FolderBytes)} из {limitGb} ГБ " +
                         $"({percent:0}%) · {stats.ClipCount} клипов · при заполнении удалятся самые старые";
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => App.OpenRecordingsFolder();
}
