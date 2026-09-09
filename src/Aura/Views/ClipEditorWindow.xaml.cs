using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Aura.Core.Library;
using Aura.Core.Logging;
using Aura.Core.Notifications;
using Aura.Core.Tools;

namespace Aura.Views;

/// <summary>
/// Небольшой встроенный монтажный стол для самого частого сценария: посмотреть
/// запись, выбрать IN/OUT и сохранить фрагмент. Он намеренно не маскируется под
/// полноценный NLE: один клип, предсказуемый результат, две честно подписанные
/// стратегии экспорта.
/// </summary>
public partial class ClipEditorWindow : Window
{
    private static readonly TimeSpan FrameStep = TimeSpan.FromSeconds(1.0 / 60);
    private readonly ClipItem _item;
    private readonly DispatcherTimer _timer;
    private TimeSpan _duration;
    private bool _playing;
    private bool _syncing;
    private CancellationTokenSource? _exportCancellation;
    private string? _ffmpeg;

    private ClipEditorWindow(ClipItem item)
    {
        _item = item;
        InitializeComponent();

        FileNameText.Text = item.FileName;
        _ffmpeg = Ffmpeg.Find(
            Services.Settings.Current.FfmpegPath,
            Services.Settings.Current.LosslessCutPath);
        if (_ffmpeg is null)
        {
            LosslessButton.ToolTip = "При первом запуске выбери ffmpeg.exe";
            ExportHint.Text = "Точный режим встроен в Windows. Для режима без потери Aura попросит ffmpeg.";
        }

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _timer.Tick += Timer_Tick;

        Loaded += Window_Loaded;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _exportCancellation?.Cancel();
            Preview.Stop();
            Preview.Source = null;
        };
    }

    public static void ShowFor(ClipItem item)
    {
        var window = new ClipEditorWindow(item);
        var owner = Application.Current?.Windows.OfType<MainWindow>()
            .FirstOrDefault(w => w.IsVisible);
        if (owner is not null) window.Owner = owner;
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.ShowInTaskbar = true;
        }
        window.ShowDialog();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _duration = await VideoEditor.ReadDurationAsync(_item.FullPath);
            if (_duration <= TimeSpan.Zero) throw new InvalidOperationException("длительность видео равна нулю");
            ConfigureTimeline(_duration);
            Preview.Source = new Uri(_item.FullPath, UriKind.Absolute);
            Preview.Pause();
        }
        catch (Exception ex)
        {
            Log.Error("Editor", ex);
            Dialogs.Say("Не удалось открыть клип", ex.Message);
            Close();
        }
    }

    private void ConfigureTimeline(TimeSpan duration)
    {
        _syncing = true;
        double seconds = duration.TotalSeconds;
        SeekSlider.Maximum = seconds;
        StartSlider.Maximum = seconds;
        EndSlider.Maximum = seconds;
        StartSlider.Value = 0;
        EndSlider.Value = seconds;
        _syncing = false;
        UpdateLabels();
    }

    private void Preview_MediaOpened(object sender, RoutedEventArgs e)
    {
        Preview.Position = TimeSpan.Zero;
        Preview.Pause();
        UpdatePreviewTime(TimeSpan.Zero);
    }

    private void Preview_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        ExportStatus.Text = "Предпросмотр недоступен";
        ExportHint.Text = e.ErrorException?.Message ?? "Windows не смог декодировать видео";
    }

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlayback();

    private void TogglePlayback()
    {
        if (_duration <= TimeSpan.Zero) return;
        if (_playing)
        {
            Preview.Pause();
            _timer.Stop();
        }
        else
        {
            if (Preview.Position.TotalSeconds < StartSlider.Value ||
                Preview.Position.TotalSeconds >= EndSlider.Value - 0.02)
                Seek(StartSlider.Value);
            Preview.Play();
            _timer.Start();
        }
        _playing = !_playing;
        PlayButton.Content = _playing ? "Ⅱ  Пауза" : "▶  Играть";
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        TimeSpan position = Preview.Position;
        if (position.TotalSeconds >= EndSlider.Value)
        {
            Preview.Pause();
            _timer.Stop();
            _playing = false;
            PlayButton.Content = "▶  Играть";
            Seek(EndSlider.Value);
            return;
        }

        _syncing = true;
        SeekSlider.Value = Math.Clamp(position.TotalSeconds, 0, SeekSlider.Maximum);
        _syncing = false;
        UpdatePreviewTime(position);
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !IsLoaded) return;
        Seek(e.NewValue);
    }

    private void StartSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        if (StartSlider.Value > EndSlider.Value - 0.05)
        {
            _syncing = true;
            StartSlider.Value = Math.Max(0, EndSlider.Value - 0.05);
            _syncing = false;
        }
        UpdateLabels();
    }

    private void EndSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        if (EndSlider.Value < StartSlider.Value + 0.05)
        {
            _syncing = true;
            EndSlider.Value = Math.Min(EndSlider.Maximum, StartSlider.Value + 0.05);
            _syncing = false;
        }
        UpdateLabels();
    }

    private void SetIn_Click(object sender, RoutedEventArgs e) => SetIn();
    private void SetOut_Click(object sender, RoutedEventArgs e) => SetOut();
    private void PreviousFrame_Click(object sender, RoutedEventArgs e) => Step(-FrameStep);
    private void NextFrame_Click(object sender, RoutedEventArgs e) => Step(FrameStep);

    private void SetIn()
    {
        StartSlider.Value = Math.Min(Preview.Position.TotalSeconds, EndSlider.Value - 0.05);
        Seek(StartSlider.Value);
    }

    private void SetOut()
    {
        EndSlider.Value = Math.Max(Preview.Position.TotalSeconds, StartSlider.Value + 0.05);
        Seek(EndSlider.Value);
    }

    private void Step(TimeSpan delta)
    {
        if (_playing) TogglePlayback();
        Seek(Math.Clamp((Preview.Position + delta).TotalSeconds, 0, _duration.TotalSeconds));
    }

    private void Seek(double seconds)
    {
        if (_duration <= TimeSpan.Zero) return;
        var position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _duration.TotalSeconds));
        Preview.Position = position;
        _syncing = true;
        SeekSlider.Value = position.TotalSeconds;
        _syncing = false;
        UpdatePreviewTime(position);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        StartSlider.Value = 0;
        EndSlider.Value = _duration.TotalSeconds;
        Seek(0);
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
        if (_exportCancellation is not null) return;
        if (_playing) TogglePlayback();

        _exportCancellation = new CancellationTokenSource();
        SetExporting(true, precise);
        ExportStatus.Text = precise ? "Точный экспорт…" : "Экспорт без потери…";
        ExportHint.Text = precise
            ? "Кодирование может занять примерно столько же, сколько длится фрагмент."
            : "Потоки копируются без изменения качества.";

        var progress = new Progress<double>(value => ExportProgress.Value = value);
        try
        {
            string output = await export(
                TimeSpan.FromSeconds(StartSlider.Value),
                TimeSpan.FromSeconds(EndSlider.Value),
                progress,
                _exportCancellation.Token);

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

    private void CancelExport_Click(object sender, RoutedEventArgs e) => _exportCancellation?.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Space) { TogglePlayback(); e.Handled = true; }
        else if (e.Key == Key.I) { SetIn(); e.Handled = true; }
        else if (e.Key == Key.O) { SetOut(); e.Handled = true; }
        else if (e.Key == Key.Left) { Step(FrameStep.Negate()); e.Handled = true; }
        else if (e.Key == Key.Right) { Step(FrameStep); e.Handled = true; }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            DragMove();
    }

    private void UpdateLabels()
    {
        if (_duration <= TimeSpan.Zero) return;
        var start = TimeSpan.FromSeconds(StartSlider.Value);
        var end = TimeSpan.FromSeconds(EndSlider.Value);
        StartTimeText.Text = "IN  " + Format(start);
        EndTimeText.Text = "OUT  " + Format(end);
        SelectionTimeText.Text = "фрагмент " + Format(end - start);
    }

    private void UpdatePreviewTime(TimeSpan position) =>
        PreviewTime.Text = $"{Format(position)} / {Format(_duration)}";

    private static string Format(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"hh\:mm\:ss\.fff")
        : value.ToString(@"mm\:ss\.fff");
}
