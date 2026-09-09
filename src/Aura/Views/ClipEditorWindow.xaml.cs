using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Aura.Core.Library;
using Aura.Core.Logging;
using Aura.Core.Notifications;
using Aura.Core.Tools;
using LibVLCSharp.Shared;
using VlcMedia = LibVLCSharp.Shared.Media;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Aura.Views;

/// <summary>
/// Встроенный монтажный стол: LibVLC отвечает только за самодостаточный
/// предпросмотр, а экспорт остаётся отдельной транзакцией и не меняет исходник.
/// </summary>
public partial class ClipEditorWindow : Window
{
    private const double MinimumSelectionSeconds = 0.05;
    private static readonly TimeSpan FrameStep = TimeSpan.FromSeconds(1.0 / 60);

    private readonly ClipItem _item;
    private readonly DispatcherTimer _timer;
    private LibVLC? _libVlc;
    private VlcMediaPlayer? _player;
    private VlcMedia? _media;
    private double _durationSeconds;
    private double _startSeconds;
    private double _endSeconds;
    private bool _pauseOnFirstFrame = true;
    private bool _disposed;
    private CancellationTokenSource? _exportCancellation;
    private string? _ffmpeg;

    private ClipEditorWindow(ClipItem item)
    {
        _item = item;
        InitializeComponent();
        FileNameText.Text = item.FileName;

        _ffmpeg = Ffmpeg.Find(Services.Settings.Current.FfmpegPath, Services.Settings.Current.LosslessCutPath);
        if (_ffmpeg is null) LosslessButton.ToolTip = "При первом запуске выбери ffmpeg.exe";

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += Timer_Tick;
        Loaded += Window_Loaded;
        Closed += (_, _) => DisposePlayer();
    }

    public static void ShowFor(ClipItem item)
    {
        var window = new ClipEditorWindow(item);
        var owner = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible);
        if (owner is not null) window.Owner = owner;
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.ShowInTaskbar = true;
        }
        window.ShowDialog();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // VideoLAN.LibVLC.Windows кладёт native runtime рядом с приложением;
            // Initialize находит его сам, установленный VLC не нужен.
            LibVLCSharp.Shared.Core.Initialize();
            _libVlc = new LibVLC("--no-video-title-show", "--quiet");
            _player = new VlcMediaPlayer(_libVlc) { EnableHardwareDecoding = true };
            _player.LengthChanged += Player_LengthChanged;
            _player.EncounteredError += Player_EncounteredError;
            _player.Playing += Player_Playing;
            _player.Paused += Player_Paused;
            _player.EndReached += Player_EndReached;
            Preview.MediaPlayer = _player;

            _media = new VlcMedia(_libVlc, new Uri(_item.FullPath));
            if (!_player.Play(_media)) throw new InvalidOperationException("LibVLC не принял файл");
            _timer.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Editor", ex);
            ShowPreviewError(ex.Message);
        }
    }

    private void Player_LengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => ConfigureTimeline(Math.Max(0, e.Length / 1000.0)));

    private void Player_EncounteredError(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => ShowPreviewError("LibVLC не смог декодировать этот файл."));

    private void Player_Playing(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_pauseOnFirstFrame)
        {
            _pauseOnFirstFrame = false;
            _player?.Pause();
            return;
        }
        PlayButton.Content = "Ⅱ  Пауза";
    });

    private void Player_Paused(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => PlayButton.Content = "▶  Играть");

    private void Player_EndReached(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        PlayButton.Content = "▶  Играть";
        Seek(_startSeconds);
    });

    private void ConfigureTimeline(double seconds)
    {
        if (seconds <= 0) return;
        bool first = _durationSeconds <= 0;
        _durationSeconds = seconds;
        if (first)
        {
            _startSeconds = 0;
            _endSeconds = seconds;
        }
        else
        {
            _startSeconds = Math.Clamp(_startSeconds, 0, seconds - MinimumSelectionSeconds);
            _endSeconds = Math.Clamp(_endSeconds, _startSeconds + MinimumSelectionSeconds, seconds);
        }
        UpdateTimelineVisuals();
        UpdatePreviewTime(CurrentSeconds);
    }

    private double CurrentSeconds => _player is null ? 0 : Math.Clamp(_player.Time / 1000.0, 0, _durationSeconds);

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlayback();

    private void TogglePlayback()
    {
        if (_player is null || _durationSeconds <= 0) return;
        if (_player.IsPlaying)
        {
            _player.Pause();
            return;
        }
        double position = CurrentSeconds;
        if (position < _startSeconds || position >= _endSeconds - 0.02) Seek(_startSeconds);
        _player.Play();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_player is null || _durationSeconds <= 0) return;
        double position = CurrentSeconds;
        if (_player.IsPlaying && position >= _endSeconds)
        {
            _player.Pause();
            Seek(_endSeconds);
            return;
        }
        UpdatePlayhead(position);
        UpdatePreviewTime(position);
    }

    private void PreviousFrame_Click(object sender, RoutedEventArgs e) => Step(-FrameStep);
    private void NextFrame_Click(object sender, RoutedEventArgs e) => Step(FrameStep);

    private void Step(TimeSpan delta)
    {
        if (_player is null) return;
        if (_player.IsPlaying) _player.Pause();
        if (delta > TimeSpan.Zero)
        {
            _player.NextFrame();
            Dispatcher.BeginInvoke(() =>
            {
                UpdatePlayhead(CurrentSeconds);
                UpdatePreviewTime(CurrentSeconds);
            }, DispatcherPriority.Background);
            return;
        }
        Seek(CurrentSeconds + delta.TotalSeconds);
    }

    private void Seek(double seconds)
    {
        if (_player is null || _durationSeconds <= 0) return;
        seconds = Math.Clamp(seconds, 0, _durationSeconds);
        _player.Time = (long)Math.Round(seconds * 1000);
        UpdatePlayhead(seconds);
        UpdatePreviewTime(seconds);
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null) return;
        _player.Mute = !_player.Mute;
        MuteButton.Content = _player.Mute ? "Звук: выкл" : "Звук: вкл";
    }

    private void Speed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_player is null || SpeedBox.SelectedItem is not ComboBoxItem { Tag: string value }) return;
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float rate))
            _player.SetRate(rate);
    }

    private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTimelineVisuals();

    private void Timeline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_durationSeconds <= 0 || FindParent<Thumb>(e.OriginalSource as DependencyObject) is not null) return;
        Seek(e.GetPosition(Timeline).X / Math.Max(1, Timeline.ActualWidth) * _durationSeconds);
    }

    private void InHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double delta = PixelsToSeconds(e.HorizontalChange);
        _startSeconds = Math.Clamp(_startSeconds + delta, 0, _endSeconds - MinimumSelectionSeconds);
        Seek(_startSeconds);
        UpdateTimelineVisuals();
    }

    private void OutHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double delta = PixelsToSeconds(e.HorizontalChange);
        _endSeconds = Math.Clamp(_endSeconds + delta, _startSeconds + MinimumSelectionSeconds, _durationSeconds);
        Seek(_endSeconds);
        UpdateTimelineVisuals();
    }

    private double PixelsToSeconds(double pixels) =>
        _durationSeconds <= 0 ? 0 : pixels / Math.Max(1, Timeline.ActualWidth - InHandle.Width) * _durationSeconds;

    private void SetIn_Click(object sender, RoutedEventArgs e) => SetIn();
    private void SetOut_Click(object sender, RoutedEventArgs e) => SetOut();

    private void SetIn()
    {
        _startSeconds = Math.Clamp(CurrentSeconds, 0, _endSeconds - MinimumSelectionSeconds);
        UpdateTimelineVisuals();
    }

    private void SetOut()
    {
        _endSeconds = Math.Clamp(CurrentSeconds, _startSeconds + MinimumSelectionSeconds, _durationSeconds);
        UpdateTimelineVisuals();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _startSeconds = 0;
        _endSeconds = _durationSeconds;
        Seek(0);
        UpdateTimelineVisuals();
    }

    private void UpdateTimelineVisuals()
    {
        double width = Timeline.ActualWidth;
        if (width <= 0 || _durationSeconds <= 0) return;

        TimelineTrack.Width = width;
        double handleWidth = InHandle.Width;
        double usable = Math.Max(1, width - handleWidth);
        double inCenter = handleWidth / 2 + _startSeconds / _durationSeconds * usable;
        double outCenter = handleWidth / 2 + _endSeconds / _durationSeconds * usable;

        Canvas.SetLeft(InHandle, inCenter - handleWidth / 2);
        Canvas.SetLeft(OutHandle, outCenter - handleWidth / 2);
        Canvas.SetLeft(DimBefore, 0); DimBefore.Width = Math.Max(0, inCenter);
        Canvas.SetLeft(SelectedRange, inCenter); SelectedRange.Width = Math.Max(0, outCenter - inCenter);
        Canvas.SetLeft(DimAfter, outCenter); DimAfter.Width = Math.Max(0, width - outCenter);

        StartTimeText.Text = Format(TimeSpan.FromSeconds(_startSeconds));
        EndTimeText.Text = Format(TimeSpan.FromSeconds(_endSeconds));
        SelectionTimeText.Text = Format(TimeSpan.FromSeconds(_endSeconds - _startSeconds));
        UpdatePlayhead(CurrentSeconds);
    }

    private void UpdatePlayhead(double seconds)
    {
        if (_durationSeconds <= 0 || Timeline.ActualWidth <= 0) return;
        Canvas.SetLeft(Playhead, seconds / _durationSeconds * Timeline.ActualWidth - Playhead.Width / 2);
    }

    private async void Precise_Click(object sender, RoutedEventArgs e) =>
        await ExportAsync((start, end, progress, ct) =>
            VideoEditor.TrimPreciseAsync(_item.FullPath, start, end, progress, ct), precise: true);

    private async void Lossless_Click(object sender, RoutedEventArgs e)
    {
        if (_ffmpeg is null)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Где лежит ffmpeg.exe",
                Filter = "ffmpeg|ffmpeg.exe|Программы (*.exe)|*.exe"
            };
            if (dialog.ShowDialog(this) != true) return;
            _ffmpeg = dialog.FileName;
            Services.Settings.Update(s => s.FfmpegPath = _ffmpeg, "tools");
            LosslessButton.ToolTip = "Очень быстро; границы по ключевым кадрам";
        }
        await ExportAsync((start, end, _, ct) =>
            Ffmpeg.TrimLosslessAsync(_ffmpeg, _item.FullPath, start, end, ct), precise: false);
    }

    private async Task ExportAsync(
        Func<TimeSpan, TimeSpan, IProgress<double>?, CancellationToken, Task<string>> export,
        bool precise)
    {
        if (_exportCancellation is not null || _durationSeconds <= 0) return;
        _player?.Pause();
        _exportCancellation = new CancellationTokenSource();
        SetExporting(true, precise);
        ExportStatus.Text = precise ? "Точный экспорт…" : "Экспорт без потери…";
        ExportHint.Text = precise ? "Точные границы, исходник остаётся нетронутым." : "Потоки копируются без изменения качества.";

        var progress = new Progress<double>(value => ExportProgress.Value = value);
        try
        {
            string output = await export(TimeSpan.FromSeconds(_startSeconds), TimeSpan.FromSeconds(_endSeconds),
                progress, _exportCancellation.Token);
            Services.Storage.RegisterSaved(output);
            ClipCommands.NotifyClipAdded(output);
            ExportProgress.Value = 1;
            ExportStatus.Text = "Фрагмент сохранён";
            ExportHint.Text = output;
            Services.Notifications.Show(NotificationKind.Saved, "Фрагмент сохранён", Path.GetFileName(output));
        }
        catch (OperationCanceledException)
        {
            ExportStatus.Text = "Экспорт отменён";
            ExportHint.Text = "Исходный клип не изменён.";
        }
        catch (Exception ex)
        {
            Log.Error("Editor", ex);
            ExportStatus.Text = "Не удалось сохранить";
            ExportHint.Text = ex.Message;
            Dialogs.Say("Ошибка экспорта", ex.Message);
        }
        finally
        {
            _exportCancellation.Dispose();
            _exportCancellation = null;
            SetExporting(false, precise);
        }
    }

    private void SetExporting(bool value, bool precise)
    {
        PreciseButton.IsEnabled = !value;
        LosslessButton.IsEnabled = !value;
        CancelExportButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        ExportProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        ExportProgress.IsIndeterminate = value && !precise;
    }

    private void ShowPreviewError(string message)
    {
        Preview.Visibility = Visibility.Collapsed;
        PreviewError.Visibility = Visibility.Visible;
        PreviewErrorText.Text = message;
    }

    private void DisposePlayer()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _exportCancellation?.Cancel();
        Preview.MediaPlayer = null;
        try { _player?.Stop(); } catch { }
        _media?.Dispose();
        _player?.Dispose();
        _libVlc?.Dispose();
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e) => _exportCancellation?.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Space) { TogglePlayback(); e.Handled = true; }
        else if (e.Key == Key.I) { SetIn(); e.Handled = true; }
        else if (e.Key == Key.O) { SetOut(); e.Handled = true; }
        else if (e.Key == Key.Left) { Step(-FrameStep); e.Handled = true; }
        else if (e.Key == Key.Right) { Step(FrameStep); e.Handled = true; }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else DragMove();
    }

    private void UpdatePreviewTime(double seconds) =>
        PreviewTime.Text = $"{Format(TimeSpan.FromSeconds(seconds))} / {Format(TimeSpan.FromSeconds(_durationSeconds))}";

    private static string Format(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"hh\:mm\:ss\.fff")
        : value.ToString(@"mm\:ss\.fff");

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}
