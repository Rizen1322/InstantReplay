using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using InstantReplay.Core.Engine;
using InstantReplay.Core.GameDetection;
using InstantReplay.Core.Hotkeys;
using InstantReplay.Core.Library;
using InstantReplay.Core.Settings;
using InstantReplay.Core.Storage;

namespace InstantReplay.Views;

/// <summary>
/// Обзор: что происходит прямо сейчас. Первая вкладка — раньше приложение открывалось
/// на настройках, и понять «пишется ли буфер, доезжает ли кодек, слышно ли микрофон»
/// можно было только по косвенным признакам.
///
/// Все живые цифры обновляются раз в секунду и ТОЛЬКО пока страница на экране:
/// таймер запускается в Loaded и гасится в Unloaded.
/// </summary>
public sealed partial class OverviewPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    // Счётчики прошлого тика — из них считаем скорость за секунду
    private long _prevAccepted, _prevEncoded, _prevDropped;
    private bool _hasPrev;
    private int _tick;

    private static readonly SolidColorBrush GreenDot = new(Windows.UI.Color.FromArgb(255, 0x4C, 0xD9, 0x64));
    private static readonly SolidColorBrush RedDot = new(Colors.OrangeRed);
    private static readonly SolidColorBrush GrayDot = new(Colors.Gray);

    public OverviewPage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();

        Loaded += (_, _) =>
        {
            _hasPrev = false;
            Refresh();
            BuildHotkeys();
            _ = LoadRecentAsync();
            Services.Storage.StatsChanged += OnStats;
            OnStats(Services.Storage.GetStats());
            Services.Engine.ReplaySaved += OnFileSaved;
            Services.Engine.RecordingSaved += OnFileSaved;
            Services.Settings.Changed += OnSettingsChanged;
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            Services.Storage.StatsChanged -= OnStats;
            Services.Engine.ReplaySaved -= OnFileSaved;
            Services.Engine.RecordingSaved -= OnFileSaved;
            Services.Settings.Changed -= OnSettingsChanged;
        };
    }

    private void OnFileSaved(string file, int seconds) =>
        Services.Dispatcher.Enqueue(() => _ = LoadRecentAsync());

    private void OnSettingsChanged(string group) => Services.Dispatcher.Enqueue(() =>
    {
        if (group is "" or "hotkeys") BuildHotkeys();
    });

    // ---------------- Живые показатели ----------------

    private void Refresh()
    {
        var engine = Services.Engine;
        var settings = Services.Settings.Current;
        bool running = engine.State != EngineState.Stopped;
        bool recording = engine.IsRecordingToFile;

        StateDot.Fill = recording ? RedDot : running ? GreenDot : GrayDot;
        StateText.Text = engine.State switch
        {
            _ when recording => "Идёт запись в файл",
            EngineState.Running => "Instant Replay пишет буфер",
            EngineState.Saving => "Сохраняю клип…",
            _ => "Выключено"
        };
        StateSub.Text = running
            ? $"Последние {FormatSeconds(settings.ReplayLengthSeconds)} всегда можно сохранить — " +
              $"{PrettyHotkey(settings.HotkeySaveReplay)}"
            : "Включи переключатель в шапке или нажми " + PrettyHotkey(settings.HotkeyToggleInstantReplay);

        // Буфер: сколько уже накоплено из заданной длины
        int buffered = (int)engine.BufferedDuration.TotalSeconds;
        int target = Math.Max(1, settings.ReplayLengthSeconds);
        BufferBar.Value = running ? Math.Clamp(buffered * 100.0 / target, 0, 100) : 0;
        BufferText.Text = running
            ? $"В буфере {FormatSeconds(buffered)} из {FormatSeconds(target)} · {ByteSize.Format(engine.BufferedBytes)}"
            : "Буфер пуст";

        UpdatePipeline(engine, settings, running);
        UpdateAudio(engine, settings, running);

        // Игру опрашиваем раз в две секунды: определение лезет в процесс переднего окна
        if (_tick++ % 2 == 0) UpdateGame();
    }

    private void UpdatePipeline(ReplayEngine engine, AppSettings settings, bool running)
    {
        var (_, accepted, encoded, dropped, _) = engine.FrameCounters;

        if (!running)
        {
            CaptureFpsText.Text = EncodeFpsText.Text = DropText.Text = "—";
            RamText.Text = "—";
            PipelineBar.IsOpen = false;
            EncoderText.Text = "Запись выключена";
            _hasPrev = false;
            return;
        }

        long dCapture = _hasPrev ? accepted - _prevAccepted : 0;
        long dEncode = _hasPrev ? encoded - _prevEncoded : 0;
        long dDrop = _hasPrev ? dropped - _prevDropped : 0;
        _prevAccepted = accepted; _prevEncoded = encoded; _prevDropped = dropped;
        bool hadPrev = _hasPrev;
        _hasPrev = true;

        CaptureFpsText.Text = hadPrev ? dCapture.ToString() : "—";
        EncodeFpsText.Text = hadPrev ? dEncode.ToString() : "—";
        DropText.Text = hadPrev ? dDrop.ToString() : "—";
        RamText.Text = ByteSize.Format(engine.BufferedBytes);

        // Потери кадров — единственная цифра, из-за которой запись выглядит рваной
        DropText.Foreground = (Brush)Application.Current.Resources[
            dDrop > 0 ? "SystemFillColorCriticalBrush" : "TextFillColorPrimaryBrush"];
        PipelineBar.IsOpen = dDrop > 0;

        var (w, h) = engine.OutputSize;
        string encoder = engine.EncoderLabel is { Length: > 0 } label ? label : "энкодер запускается";
        string size = w > 0 ? $"{w}×{h}" : $"{settings.VerticalResolution}p";
        EncoderText.Text = $"{encoder} · {size} · {settings.Fps} FPS · {settings.BitrateMbps} Мбит/с · {settings.Codec}";
    }

    private void UpdateAudio(ReplayEngine engine, AppSettings settings, bool running)
    {
        var (game, mic) = running ? engine.AudioLevels : (0f, 0f);
        GameLevel.Value = Math.Clamp(game, 0, 1);
        MicLevel.Value = Math.Clamp(mic, 0, 1);

        string mode = settings.TrackMode switch
        {
            AudioTrackMode.Separate => "две отдельные дорожки (игра и микрофон)",
            AudioTrackMode.GameOnly => "только звук игры",
            AudioTrackMode.MicOnly => "только микрофон",
            _ => "одна смикшированная дорожка"
        };
        string sources = (settings.CaptureGameAudio ? "игра" : "")
                       + (settings.CaptureGameAudio && settings.CaptureMicrophone ? " + " : "")
                       + (settings.CaptureMicrophone ? "микрофон" : "");
        if (sources.Length == 0) sources = "звук не записывается";
        AudioModeText.Text = $"Источники: {sources} · В файле: {mode}";
    }

    private void UpdateGame()
    {
        string game = GameDetector.DetectForegroundGame();
        GameText.Text = game;
        GameHint.Text = Services.Settings.Current.GroupByGame
            ? $"Следующий клип ляжет в папку «{game}»"
            : "Раскладка по папкам игр выключена";
    }

    // ---------------- Хранилище ----------------

    private void OnStats(StorageStats stats) => Services.Dispatcher.Enqueue(() =>
    {
        int limitGb = Services.Settings.Current.MaxFolderSizeGb;
        StorageMain.Text = $"{ByteSize.Format(stats.FolderBytes)} · {stats.ClipCount} клипов";

        if (limitGb > 0)
        {
            long limit = limitGb * 1024L * 1024 * 1024;
            double percent = Math.Clamp(stats.FolderBytes * 100.0 / limit, 0, 100);
            StorageBar.Visibility = Visibility.Visible;
            StorageBar.Value = percent;
            StorageBar.Foreground = (Brush)Application.Current.Resources[
                percent >= 90 ? "SystemFillColorCriticalBrush"
                : percent >= 70 ? "SystemFillColorCautionBrush"
                : "SystemFillColorSuccessBrush"];
            StorageSub.Text = $"{percent:0}% от лимита {limitGb} ГБ · свободно на диске {ByteSize.Format(stats.FreeDiskBytes)}";
        }
        else
        {
            StorageBar.Visibility = Visibility.Collapsed;
            StorageSub.Text = $"Лимит папки не задан · свободно на диске {ByteSize.Format(stats.FreeDiskBytes)}";
        }
    });

    // ---------------- Хоткеи ----------------

    private void BuildHotkeys()
    {
        HotkeysPanel.Children.Clear();
        foreach (var binding in HotkeyConflicts.Bindings(Services.Settings.Current))
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(new TextBlock { Text = binding.Title, FontSize = 13 });

            var combo = new TextBlock
            {
                Text = PrettyHotkey(binding.Combo),
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources[
                    binding.Combo.Length > 0 ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"]
            };
            Grid.SetColumn(combo, 1);
            grid.Children.Add(combo);

            HotkeysPanel.Children.Add(grid);
        }
    }

    private static string PrettyHotkey(string combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return "не задано";
        return string.Join(" + ", combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

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
