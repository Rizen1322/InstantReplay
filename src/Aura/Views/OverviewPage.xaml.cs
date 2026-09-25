using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Aura.Controls;
using Aura.Core.Engine;
using Aura.Core.Library;

namespace Aura.Views;

/// <summary>
/// Главный экран: живой кадр того, что пишет повтор, лента буфера, управление
/// повтором и последние клипы.
///
/// Живой кадр берётся у работающего конвейера раз в две секунды и только пока
/// этот экран открыт: своя сессия захвата ради картинки не поднимается, а на
/// свёрнутом окне и в других разделах кадр не вычитывается вовсе.
/// </summary>
public partial class OverviewPage : PageBase
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _frameTick = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _loadedClips;
    private bool _grabbing;
    private double _gameDb = LevelMeter.FloorDb, _micDb = LevelMeter.FloorDb;
    private int _recentColumns = 5;

    /// <summary>Выделение на ленте: доля от левого края, с которой оно начинается (1 — нет выделения).</summary>
    private double _pickStart = 1;
    private bool _picking;

    public override string Title => "Повтор";

    public override bool FillsWindow => true;

    public OverviewPage()
    {
        InitializeComponent();

        _tick.Tick += (_, _) => Refresh();
        _frameTick.Tick += async (_, _) => await GrabFrameAsync();
        ClipCommands.LibraryChanged += () => Dispatcher.BeginInvoke(() => _ = LoadRecentAsync());
        ClipCommands.ClipAdded += path => Dispatcher.BeginInvoke(() => { _ = path; _ = LoadRecentAsync(); });
        ReplayFilmstrip.Changed += () => Dispatcher.BeginInvoke(BuildStrip);

        SizeChanged += (_, _) => Relayout();

        // Клик по карточке в «Последних» открывает файл, как в «Клипах»
        Recent.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(Card_Click));
    }

    public override void OnShown()
    {
        TipText.Text = Tips.Next("overview");
        Refresh();
        BuildStrip();
        _tick.Start();
        _frameTick.Start();
        _ = GrabFrameAsync();
        if (!_loadedClips) _ = LoadRecentAsync();
    }

    public override void OnHidden()
    {
        _tick.Stop();
        _frameTick.Stop();
    }

    /// <summary>Узкое окно: панель управления уже, клипов в ряду меньше.</summary>
    private void Relayout()
    {
        double width = ActualWidth;
        ControlCol.Width = new GridLength(width < 760 ? 260 : 300);
        int columns = width < 700 ? 3 : width < 940 ? 4 : 5;
        if (columns == _recentColumns) return;
        _recentColumns = columns;
        _ = LoadRecentAsync();
    }

    // ---------------- Состояние ----------------

    private void Refresh()
    {
        var engine = Services.Engine;
        bool on = engine.State != EngineState.Stopped;
        var s = Services.Settings.Current;

        Master.IsChecked = on;
        var buffered = engine.BufferedDuration;
        BufferTime.Text = Format(buffered);
        BufferOf.Text = $"из {Format(TimeSpan.FromSeconds(s.ReplayLengthSeconds))}";
        BufferTime.Foreground = (Brush)FindResource(on ? "TxBrush" : "Tx3Brush");

        LengthText.Text = LengthWords(s.ReplayLengthSeconds);
        QualityText.Text = $"{Resolution(s.VerticalResolution)}{s.Fps}, {s.BitrateMbps} Мбит/с";
        SoundText.Text = Sound(s);
        SourceText.Text = $"Экран {s.MonitorIndex + 1}";

        SaveKey.Text = s.HotkeySaveReplay;
        SaveKeyBox.Visibility = string.IsNullOrEmpty(s.HotkeySaveReplay) ? Visibility.Collapsed : Visibility.Visible;
        Save30Key.Text = s.HotkeySaveLast30;
        SaveButton.IsEnabled = on;
        Save30.IsEnabled = on;

        bool recording = engine.IsRecordingToFile;
        RecordLabel.Text = recording ? "Стоп" : "Запись";
        RecordDot.Fill = (Brush)FindResource(recording ? "TxBrush" : "RecBrush");
        RecordButton.IsEnabled = on || recording;

        FrameIdle.Visibility = on && FrameImage.Background is ImageBrush ? Visibility.Collapsed : Visibility.Visible;
        IdleTitle.Text = on ? "Жду кадр" : "Повтор выключен";
        IdleSub.Text = on ? "Картинка появится через пару секунд" : "Включи, чтобы не упустить момент";
        if (!on) FrameImage.Background = null;

        // Шкала ленты: слева самое старое, что лежит в буфере
        double total = Math.Max(1, buffered.TotalSeconds);
        Scale.Visibility = on && buffered.TotalSeconds >= 3 ? Visibility.Visible : Visibility.Hidden;
        Scale0.Text = "−" + Format(TimeSpan.FromSeconds(total));
        Scale1.Text = "−" + Format(TimeSpan.FromSeconds(total * 2 / 3));
        Scale2.Text = "−" + Format(TimeSpan.FromSeconds(total / 3));
        UpdatePick();

        ShowLevels(on, s);
    }

    private void ShowLevels(bool on, Core.Settings.AppSettings s)
    {
        GameMeterRow.Visibility = s.CaptureGameAudio ? Visibility.Visible : Visibility.Collapsed;
        MicMeterRow.Visibility = s.CaptureMicrophone ? Visibility.Visible : Visibility.Collapsed;
        var (game, mic) = on ? Services.Engine.AudioLevels : (0f, 0f);
        _gameDb = Smooth(_gameDb, LevelMeter.ToDb(game));
        _micDb = Smooth(_micDb, LevelMeter.ToDb(mic));
        GameMeter.LevelDb = _gameDb;
        MicMeter.LevelDb = _micDb;
        GameDb.Text = DbText(_gameDb);
        MicDb.Text = DbText(_micDb);
    }

    private static double Smooth(double shown, double now) => now > shown ? now : Math.Max(now, shown - 2);

    private static string DbText(double db) =>
        db <= LevelMeter.FloorDb + 0.5 ? "тихо" : $"−{Math.Abs(Math.Round(db)):0} дБ";

    // ---------------- Живой кадр и лента ----------------

    private async Task GrabFrameAsync()
    {
        if (_grabbing || Services.Engine.State == EngineState.Stopped) return;
        if (Window.GetWindow(this) is not { IsVisible: true, WindowState: not WindowState.Minimized }) return;
        _grabbing = true;
        try
        {
            var image = await LivePreview.GrabAsync(960);
            if (image is null) return;
            FrameImage.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            FrameIdle.Visibility = Visibility.Collapsed;
        }
        finally { _grabbing = false; }
    }

    private void BuildStrip()
    {
        var frames = ReplayFilmstrip.Snapshot();
        // Не больше восьми: на ленте шириной 600 px кадр уже меньше спички
        if (frames.Count > 8) frames = frames.Skip(frames.Count - 8).ToList();
        StripFrames.Children.Clear();
        StripFrames.Columns = Math.Max(1, frames.Count);
        foreach (var frame in frames)
            StripFrames.Children.Add(new Border
            {
                Margin = new Thickness(0, 0, 2, 0),
                Opacity = 0.6,
                Background = new ImageBrush(frame.Image) { Stretch = Stretch.UniformToFill }
            });
        StripEmpty.Visibility = frames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Strip_Down(object sender, MouseButtonEventArgs e)
    {
        if (Services.Engine.State == EngineState.Stopped) return;
        _picking = true;
        Strip.CaptureMouse();
        SetPick(e.GetPosition(Strip).X);
    }

    private void Strip_Move(object sender, MouseEventArgs e)
    {
        if (_picking) SetPick(e.GetPosition(Strip).X);
    }

    private void Strip_Up(object sender, MouseButtonEventArgs e)
    {
        _picking = false;
        Strip.ReleaseMouseCapture();
    }

    private void SetPick(double x)
    {
        _pickStart = Math.Clamp(x / Math.Max(1, Strip.ActualWidth), 0, 1);
        UpdatePick();
    }

    /// <summary>Сколько секунд выделено на ленте (от начала выделения до «сейчас»).</summary>
    private int PickSeconds()
    {
        double total = Services.Engine.BufferedDuration.TotalSeconds;
        return (int)Math.Round(total * (1 - _pickStart));
    }

    private void UpdatePick()
    {
        int seconds = PickSeconds();
        bool has = seconds >= 2 && _pickStart < 0.995;
        Pick.Width = has ? Strip.ActualWidth * (1 - _pickStart) : 0;
        SavePick.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        SavePick.Content = $"Сохранить {Format(TimeSpan.FromSeconds(seconds))}";
        PickInfo.Text = has
            ? $"Выделено {Format(TimeSpan.FromSeconds(seconds))} до текущего момента"
            : "Потяни по ленте, чтобы сохранить только последние секунды";
    }

    private void SavePick_Click(object sender, RoutedEventArgs e)
    {
        int seconds = PickSeconds();
        if (seconds < 2) return;
        Services.Engine.SaveReplay(seconds);
        _pickStart = 1;
        UpdatePick();
    }

    // ---------------- Последние клипы ----------------

    private async Task LoadRecentAsync()
    {
        var s = Services.Settings.Current;
        string root = s.SaveRootPath, shots = s.ScreenshotFolder;
        int take = _recentColumns;
        var all = await Task.Run(() => ClipLibrary.ScanAll(root, shots));
        var items = all.Take(take).ToList();

        _loadedClips = true;
        Recent.ItemsSource = items;
        if (Recent.ItemsPanel is not null)
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (FindPanel(Recent) is UniformGrid grid) grid.Columns = take;
            }, DispatcherPriority.Loaded);
        RecentEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int today = all.Count(i => i.Created.Date == DateTime.Today);
        RecentCount.Text = today > 0 ? $"сегодня {today}" : "";

        foreach (var item in items) _ = ClipThumbnails.LoadAsync(item);
    }

    private static Panel? FindPanel(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is UniformGrid grid) return grid;
            if (FindPanel(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>Перечитать список после сохранения нового клипа.</summary>
    public void ReloadRecent() => _ = LoadRecentAsync();

    // ---------------- Действия ----------------

    private void Master_Click(object sender, RoutedEventArgs e)
    {
        App.ToggleEngine();
        Refresh();
    }

    private void SaveReplay_Click(object sender, RoutedEventArgs e) => Services.Engine.SaveReplay();

    private void Save30_Click(object sender, RoutedEventArgs e) => Services.Engine.SaveReplay(30);

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (Services.Engine.IsRecordingToFile) Services.Engine.StopRecordingToFile();
        else App.SafeStartRecording();
        Refresh();
    }

    private void Go_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page })
            (Window.GetWindow(this) as MainWindow)?.Navigate(page);
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

    // ---------------- Текст ----------------

    private static string Resolution(int height) => height >= 2160 ? "4K" : $"{height}p";

    private static string Sound(Core.Settings.AppSettings s) =>
        s.CaptureGameAudio && s.CaptureMicrophone ? "Игра и микрофон"
        : s.CaptureGameAudio ? "Только игра"
        : s.CaptureMicrophone ? "Только микрофон" : "Без звука";

    private static string LengthWords(int seconds) =>
        seconds < 60 ? $"{seconds} с" : seconds % 60 == 0 ? $"{seconds / 60} мин" : $"{seconds / 60} мин {seconds % 60} с";

    private static string Format(TimeSpan span) =>
        span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : $"{(int)span.TotalMinutes}:{span.Seconds:00}";
}
