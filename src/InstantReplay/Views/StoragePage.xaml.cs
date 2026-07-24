using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using InstantReplay.Core.Storage;

namespace InstantReplay.Views;

public sealed partial class StoragePage : Page
{
    public StoragePage()
    {
        InitializeComponent();
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
        var s = Services.Settings.Current;
        PathText.Text = s.SaveRootPath;
        GroupToggle.IsOn = s.GroupByGame;
        TemplateBox.Text = s.FileNameTemplate;
        MaxFolderBox.Value = s.MaxFolderSizeGb;
        MinFreeBox.Value = s.MinFreeSpaceGb;
        AutoDeleteToggle.IsOn = s.AutoDeleteOldClips;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var s = Services.Settings.Current;
        s.GroupByGame = GroupToggle.IsOn;
        if (!string.IsNullOrWhiteSpace(TemplateBox.Text)) s.FileNameTemplate = TemplateBox.Text.Trim();
        s.MaxFolderSizeGb = (int)MaxFolderBox.Value;
        s.MinFreeSpaceGb = (int)MinFreeBox.Value;
        s.AutoDeleteOldClips = AutoDeleteToggle.IsOn;
        Services.Settings.Save("storage");

        ApplyBtn.Content = "Применено ✓";
        _ = ResetApplyLabelAsync();
    }

    private async Task ResetApplyLabelAsync()
    {
        await Task.Delay(1500);
        ApplyBtn.Content = "Применить";
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        // WinUI 3 desktop: пикеру нужен HWND главного окна
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(WindowTracker.Main!);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        PathText.Text = folder.Path;
        Services.Settings.Current.SaveRootPath = folder.Path;
        Services.Settings.Save("storage"); // StorageManager пересчитает статистику по новой папке
    }

    private void OnStats(StorageStats stats) => Services.Dispatcher.Enqueue(() =>
    {
        SavedCountText.Text = Services.Settings.Current.TotalReplaysSaved.ToString();
        UsedText.Text = StorageManager.FormatBytes(stats.FolderBytes);
        FreeText.Text = StorageManager.FormatBytes(stats.FreeDiskBytes);
        PathText.Text = stats.RootPath;
        UpdateUsageBar(stats);
    });

    /// <summary>
    /// Полоса «сколько из лимита папки занято». Цифры рядом были, но по ним не
    /// видно, близко ли автоочистка — теперь видно с одного взгляда.
    /// </summary>
    private void UpdateUsageBar(StorageStats stats)
    {
        int limitGb = Services.Settings.Current.MaxFolderSizeGb;
        if (limitGb <= 0) // 0 = без лимита, показывать нечего
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

        UsageText.Text = $"{StorageManager.FormatBytes(stats.FolderBytes)} из {limitGb} ГБ " +
                         $"({percent:0}%) · {stats.ClipCount} клипов" +
                         (Services.Settings.Current.AutoDeleteOldClips
                             ? " · при заполнении удалятся самые старые"
                             : " · автоудаление выключено");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => App.OpenRecordingsFolder();
}
