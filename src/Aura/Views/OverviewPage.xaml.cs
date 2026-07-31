using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Aura.Core.Engine;
using Aura.Core.Library;
using Aura.Core.Storage;

namespace Aura.Views;

/// <summary>
/// Главный экран: что происходит прямо сейчас, четыре частых действия,
/// три цифры о состоянии и последние записи.
/// </summary>
public partial class OverviewPage : PageBase
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _loadedClips;

    public override string Title => "Обзор";

    public override UIElement[] ToolbarActions
    {
        get
        {
            var button = new Button { Style = (Style)FindResource("BtnPri"), Content = "Сохранить повтор" };
            button.Click += SaveReplay_Click;
            return [button];
        }
    }

    public OverviewPage()
    {
        InitializeComponent();

        _tick.Tick += (_, _) => Refresh();
        Services.Storage.StatsChanged += stats => Dispatcher.BeginInvoke(() => ShowStats(stats));
        ClipCommands.LibraryChanged += () => Dispatcher.BeginInvoke(() => _ = LoadRecentAsync());

        SizeChanged += (_, _) =>
        {
            // Узкое окно — плитки в две колонки, совсем узкое — в одну.
            // Rows остаётся нулём: строки UniformGrid считает сам по числу колонок.
            double width = ActualWidth;
            Actions.Columns = width < 460 ? 1 : width < 720 ? 2 : 4;
            Stats.Columns = width < 560 ? 1 : width < 760 ? 2 : 3;
        };

        // Клик по карточке в «Последних записях» открывает файл — как в «Клипах»
        Recent.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
                          new RoutedEventHandler(Card_Click));
    }

    public override void OnShown()
    {
        SyncHotkeys();
        Refresh();
        ShowStats(Services.Storage.GetStats());
        _tick.Start();
        if (!_loadedClips) _ = LoadRecentAsync();
    }

    public override void OnHidden() => _tick.Stop();

    // ---------------- Состояние ----------------

    private void Refresh()
    {
        var engine = Services.Engine;
        bool on = engine.State != EngineState.Stopped;
        var settings = Services.Settings.Current;

        Master.IsChecked = on;
        MasterState.Text = on ? "Включён" : "Выключен";
        Tally.Fill = (Brush)FindResource(on ? "RecBrush" : "Tx3Brush");

        string game = Core.GameDetection.GameDetector.DetectForegroundGame();
        Eyebrow.Text = on ? $"Запись идёт · {game}" : "Повтор выключен";

        var buffered = engine.BufferedDuration;
        BufferTime.Text = Format(buffered);
        TickLeft.Text = "−" + Format(buffered);

        BufferHint.Text = on ? "сохранится по" : "включи, чтобы не упустить момент";
        SaveCombo.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        double target = settings.ReplayLengthSeconds;
        double part = target > 0 ? Math.Min(1, buffered.TotalSeconds / target) : 0;
        AnimateWidth(BufferFill, part, BufferFill.ActualWidth);

        RecName.Text = engine.IsRecordingToFile ? "Остановить запись" : "Начать запись";
        RecTile.Data = (Geometry)FindResource(engine.IsRecordingToFile ? "Ico.Stop" : "Ico.Rec");
        RecTile.Background = (Brush)FindResource(engine.IsRecordingToFile ? "GrayBrush" : "RecBrush");

        QualityText.Text = $"{settings.VerticalResolution}p · {settings.Fps}";
        QualitySub.Text = $"{Codec(settings.Codec)} · {settings.BitrateMbps} Мбит/с";

        BuildPills(on, game);
    }

    /// <summary>Ширина шкалы задаётся анимацией, чтобы буфер «наливался», а не прыгал.</summary>
    private void AnimateWidth(FrameworkElement element, double part, double current)
    {
        double full = ((FrameworkElement)element.Parent).ActualWidth;
        double to = Math.Max(0, full * part);
        if (Math.Abs(to - current) < 0.5) return;
        element.BeginAnimation(WidthProperty, new DoubleAnimation(to, TimeSpan.FromSeconds(0.45))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void BuildPills(bool on, string game)
    {
        var settings = Services.Settings.Current;
        var items = new List<(string Icon, string Text)>
        {
            ("Ico.Monitor", $"Экран {settings.MonitorIndex + 1}"),
            ("", $"{settings.VerticalResolution}p · {settings.Fps} кадров"),
            ("Ico.Speaker", Sound(settings))
        };
        if (on && game.Length > 0) items.Insert(0, ("Ico.Play", game));

        // Перестраиваем только при изменении состава — иначе мигало бы каждые 400 мс
        string signature = string.Join("|", items.Select(i => i.Text));
        if ((string?)Pills.Tag == signature) return;
        Pills.Tag = signature;

        Pills.Children.Clear();
        foreach (var (icon, text) in items)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (icon.Length > 0)
                content.Children.Add(new Controls.Icon
                {
                    Data = (Geometry)FindResource(icon),
                    Size = 12,
                    Foreground = (Brush)FindResource("Tx2Brush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                });
            content.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("PillText") });

            Pills.Children.Add(new Border
            {
                Style = (Style)FindResource("Pill"),
                Margin = new Thickness(0, 0, 7, 7),
                Child = content
            });
        }
    }

    private static string Sound(Core.Settings.AppSettings s) =>
        s.CaptureGameAudio && s.CaptureMicrophone ? "Игра и микрофон"
        : s.CaptureGameAudio ? "Только игра"
        : s.CaptureMicrophone ? "Только микрофон" : "Без звука";

    private static string Codec(Core.Settings.VideoCodec codec) => codec switch
    {
        Core.Settings.VideoCodec.HEVC => "HEVC",
        Core.Settings.VideoCodec.AV1 => "AV1",
        _ => "H.264"
    };

    private static string Format(TimeSpan span) =>
        span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : $"{(int)span.TotalMinutes}:{span.Seconds:00}";

    private void SyncHotkeys()
    {
        var s = Services.Settings.Current;
        SaveCombo.Combo = ComboSave.Combo = s.HotkeySaveReplay;
        Combo30.Combo = s.HotkeySaveLast30;
        ComboShot.Combo = s.HotkeyScreenshot;
        ComboRec.Combo = Services.Engine.IsRecordingToFile ? s.HotkeyStopRecording : s.HotkeyStartRecording;
    }

    private void ShowStats(StorageStats stats)
    {
        var settings = Services.Settings.Current;
        long limit = settings.MaxFolderSizeGb * 1024L * 1024 * 1024;
        bool hasLimit = settings.AutoDeleteOldClips && limit > 0;

        UsedText.Text = ByteSize.Format(stats.FolderBytes);
        FreeText.Text = hasLimit
            ? $"из {settings.MaxFolderSizeGb} ГБ · свободно {ByteSize.Format(stats.FreeDiskBytes)}"
            : $"свободно на диске {ByteSize.Format(stats.FreeDiskBytes)}";

        // Полоса — только когда есть чем ограничиваться
        UsedBar.Visibility = hasLimit ? Visibility.Visible : Visibility.Collapsed;
        if (hasLimit)
        {
            double part = Math.Min(1, stats.FolderBytes / (double)limit);
            UsedFill.BeginAnimation(WidthProperty,
                new DoubleAnimation(((FrameworkElement)UsedFill.Parent).ActualWidth * part, TimeSpan.FromSeconds(0.7))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        ClipCountText.Text = stats.ClipCount.ToString();
        ClipCountSub.Text = "в папке с записями";
    }

    // ---------------- Последние записи ----------------

    private async Task LoadRecentAsync()
    {
        string root = Services.Settings.Current.SaveRootPath;
        var items = await Task.Run(() => ClipLibrary.Scan(root).Take(4).ToList());

        _loadedClips = true;
        Recent.ItemsSource = items;
        RecentEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in items) _ = ClipThumbnails.LoadAsync(item);
    }

    /// <summary>Перечитать список после сохранения нового клипа.</summary>
    public void ReloadRecent() => _ = LoadRecentAsync();

    // ---------------- Действия ----------------

    private void Master_Click(object sender, RoutedEventArgs e)
    {
        App.ToggleEngine();
        Refresh();
    }

    private void SaveReplay_Click(object sender, RoutedEventArgs e)
    {
        Services.Engine.SaveReplay();
    }

    private void SaveLast30_Click(object sender, RoutedEventArgs e)
    {
        Services.Engine.SaveReplay(30);
    }

    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        await App.TakeScreenshotAsync();
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (Services.Engine.IsRecordingToFile) Services.Engine.StopRecordingToFile();
        else App.SafeStartRecording();
        Refresh();
        SyncHotkeys();
    }

    private void AllClips_Click(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.Navigate("clips");

    /// <summary>
    /// Открываем через explorer.exe, а не напрямую: приложение работает с правами
    /// администратора, а упакованные плееры Windows из такого процесса
    /// активируются криво и жалуются на «файл не найден».
    /// </summary>
    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: ClipItem clip }) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{clip.FullPath}\"")
                { UseShellExecute = true });
        }
        catch { }
    }
}
