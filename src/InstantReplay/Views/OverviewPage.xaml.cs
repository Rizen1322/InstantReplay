using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using InstantReplay.Core.Engine;
using InstantReplay.Core.GameDetection;
using InstantReplay.Core.Library;
using InstantReplay.Core.Settings;
using InstantReplay.Core.Storage;

namespace InstantReplay.Views;

/// <summary>
/// Обзор: что происходит прямо сейчас. Первая вкладка — раньше приложение открывалось
/// на настройках, и понять «пишется ли буфер, доезжает ли кодек, сколько занято»
/// можно было только по косвенным признакам.
///
/// Живые цифры обновляются раз в секунду и ТОЛЬКО пока страница на экране:
/// таймер запускается в Loaded и гасится в Unloaded.
/// </summary>
public sealed partial class OverviewPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private int _tick;

    private static readonly SolidColorBrush GreenDot = new(Windows.UI.Color.FromArgb(255, 0x4C, 0xD9, 0x64));
    private static readonly SolidColorBrush RedDot = new(Colors.OrangeRed);
    private static readonly SolidColorBrush BlueDot = new(Windows.UI.Color.FromArgb(255, 0x0A, 0x84, 0xFF));
    private static readonly SolidColorBrush GrayDot = new(Colors.Gray);

    public OverviewPage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();

        Loaded += (_, _) =>
        {
            Refresh();
            _ = LoadRecentAsync();
            Services.Storage.StatsChanged += OnStats;
            OnStats(Services.Storage.GetStats());
            Services.Engine.ReplaySaved += OnFileSaved;
            Services.Engine.RecordingSaved += OnFileSaved;
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            Services.Storage.StatsChanged -= OnStats;
            Services.Engine.ReplaySaved -= OnFileSaved;
            Services.Engine.RecordingSaved -= OnFileSaved;
        };
    }

    private void OnFileSaved(string file, int seconds) =>
        Services.Dispatcher.Enqueue(() => _ = LoadRecentAsync());

    // ---------------- Живые показатели ----------------

    private void Refresh()
    {
        var engine = Services.Engine;
        var settings = Services.Settings.Current;
        bool running = engine.State != EngineState.Stopped;
        bool recording = engine.IsRecordingToFile;
        bool saving = engine.State == EngineState.Saving;

        StateDot.Fill = saving ? BlueDot : recording ? RedDot : running ? GreenDot : GrayDot;
        StateText.Text = engine.State switch
        {
            EngineState.Saving => $"Сохраняю клип… {engine.SaveProgress * 100:0}%",
            _ when recording => "Идёт запись в файл",
            EngineState.Running => "Instant Replay пишет буфер",
            _ => "Выключено"
        };
        StateSub.Text = StateSubtitle(settings, running, saving);
        UpdateQuickActions(settings, running, recording);

        // Буфер: сколько уже накоплено из заданной длины
        int buffered = (int)engine.BufferedDuration.TotalSeconds;
        int target = Math.Max(1, settings.ReplayLengthSeconds);
        BufferBar.Value = running ? Math.Clamp(buffered * 100.0 / target, 0, 100) : 0;
        BufferLabel.Text = running
            ? $"Буфер повтора · до {FormatSeconds(target)}"
            : "Буфер повтора";
        BufferText.Text = running
            ? $"{FormatSeconds(buffered)} · {ByteSize.Format(engine.BufferedBytes)}"
            : "пуст";

        // Игру опрашиваем раз в две секунды: определение лезет в процесс переднего окна
        if (_tick++ % 2 == 0) UpdateGame();
    }

    // ---------------- Быстрые действия ----------------

    private void UpdateQuickActions(AppSettings settings, bool running, bool recording)
    {
        bool canSave = Services.Engine.State == EngineState.Running;
        SaveReplayBtn.IsEnabled = canSave;
        Save30Btn.IsEnabled = canSave;

        SaveReplayHint.Text = HotkeyHint(settings.HotkeySaveReplay, $"весь буфер · {FormatSeconds(settings.ReplayLengthSeconds)}");
        Save30Hint.Text = HotkeyHint(settings.HotkeySaveLast30, "короткий клип");

        RecordText.Text = recording ? "Остановить запись" : "Начать запись";
        RecordHint.Text = HotkeyHint(
            recording ? settings.HotkeyStopRecording : settings.HotkeyStartRecording,
            recording ? "идёт запись в файл" : "обычная запись в файл");
        RecordDot.Fill = recording ? RedDot : GrayDot;
    }

    private static string HotkeyHint(string combo, string fallback) =>
        string.IsNullOrWhiteSpace(combo) ? fallback : Pretty(combo);

    private void SaveReplay_Click(object sender, RoutedEventArgs e) => Services.Engine.SaveReplay();

    private void SaveLast30_Click(object sender, RoutedEventArgs e) => Services.Engine.SaveReplay(30);

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Services.Engine.IsRecordingToFile) Services.Engine.StopRecordingToFile();
            else Services.Engine.StartRecordingToFile();
        }
        catch (Exception ex)
        {
            _ = new ContentDialog
            {
                Title = "Не удалось начать запись",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            }.ShowAsync();
        }
        Refresh();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => App.OpenRecordingsFolder();

    /// <summary>Подпись под статусом: подсказывает следующий шаг, а не повторяет статус.</summary>
    private static string StateSubtitle(AppSettings settings, bool running, bool saving)
    {
        if (saving) return "Клип дописывается на диск — запись буфера продолжается";
        if (!running) return "Включите переключатель в шапке окна, чтобы копить последние минуты игры";

        string hotkey = settings.HotkeySaveReplay.Trim();
        return hotkey.Length > 0
            ? $"Последние {FormatSeconds(settings.ReplayLengthSeconds)} сохранятся по {Pretty(hotkey)}"
            : $"Последние {FormatSeconds(settings.ReplayLengthSeconds)} можно сохранить кнопкой в шапке окна";
    }

    private static string Pretty(string combo) =>
        string.Join(" + ", combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    private void UpdateGame()
    {
        GameText.Text = GameDetector.DetectForegroundGame();
    }

    // ---------------- Хранилище ----------------

    private void OnStats(StorageStats stats) => Services.Dispatcher.Enqueue(() =>
    {
        var settings = Services.Settings.Current;
        StorageMain.Text = $"{ByteSize.Format(stats.FolderBytes)} · {stats.ClipCount} клипов";
        StorageFree.Text = $"свободно {ByteSize.Format(stats.FreeDiskBytes)}";

        // Полоса лимита имеет смысл только когда включено автоудаление: иначе лимит
        // ни на что не влияет, и шкала «занято из N ГБ» только путает.
        int limitGb = settings.MaxFolderSizeGb;
        if (settings.AutoDeleteOldClips && limitGb > 0)
        {
            long limit = limitGb * 1024L * 1024 * 1024;
            double percent = Math.Clamp(stats.FolderBytes * 100.0 / limit, 0, 100);
            StorageBar.Visibility = Visibility.Visible;
            StorageBar.Value = percent;
            StorageBar.Foreground = (Brush)Application.Current.Resources[
                percent >= 90 ? "SystemFillColorCriticalBrush"
                : percent >= 70 ? "SystemFillColorCautionBrush"
                : "SystemFillColorSuccessBrush"];
            StorageSub.Text = $"{percent:0}% от лимита {limitGb} ГБ · при заполнении удалятся самые старые";
        }
        else
        {
            StorageBar.Visibility = Visibility.Collapsed;
            StorageSub.Text = settings.SaveRootPath;
        }
    });

    // ---------------- Последние записи ----------------

    private async Task LoadRecentAsync()
    {
        string root = Services.Settings.Current.SaveRootPath;
        var items = await Task.Run(() => ClipLibrary.Scan(root));
        var recent = items.Take(4).ToList();

        RecentPanel.Children.Clear();
        RecentEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in recent)
        {
            RecentPanel.Children.Add(BuildRecentCard(item));
            _ = ClipThumbnails.LoadAsync(item);
        }
    }

    /// <summary>Мини-карточка последней записи: кадр, имя, время. Клик — открыть панораму.</summary>
    private Button BuildRecentCard(ClipItem item)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        // Миниатюра приезжает асинхронно — обновляем картинку по событию модели
        item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ClipItem.Thumbnail)) return;
            Services.Dispatcher.Enqueue(() => image.Source = item.Thumbnail);
        };
        if (item.Thumbnail is not null) image.Source = item.Thumbnail;

        var thumb = new Border
        {
            Width = 176,
            Height = 99,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            Child = image
        };

        var panel = new StackPanel { Spacing = 6, Width = 176 };
        panel.Children.Add(thumb);
        panel.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 12.5,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        panel.Children.Add(new TextBlock
        {
            Text = item.Subtitle,
            FontSize = 11,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });

        var button = new Button
        {
            Content = panel,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8)
        };
        ToolTipService.SetToolTip(button, item.FileName);
        button.Click += (_, _) => OpenClips();
        return button;
    }

    private void OpenClips_Click(object sender, RoutedEventArgs e) => OpenClips();

    private static void OpenClips() => (WindowTracker.Main as MainWindow)?.SelectTab(typeof(ClipsPage));

    private static string FormatSeconds(int seconds) =>
        seconds >= 60
            ? seconds % 60 == 0 ? $"{seconds / 60} мин" : $"{seconds / 60} мин {seconds % 60} сек"
            : $"{seconds} сек";
}
