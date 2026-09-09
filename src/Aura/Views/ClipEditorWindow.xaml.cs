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
    private VlcMediaPlayer? _mixedAudioPlayer;
    private VlcMedia? _mixedAudioMedia;
    private int[] _audioTrackIds = [];
    // -1 = свести все дорожки, 0..N = оставить одну дорожку.
    private int _audioSelection = -1;
    private double _durationSeconds;
    private double _startSeconds;
    private double _endSeconds;
    private bool _pauseOnFirstFrame = true;
    private bool _isMuted;
    private bool _disposed;
    private long _lastAudioSyncTick;
    private CancellationTokenSource? _exportCancellation;
    private readonly CancellationTokenSource _previewMixCancellation = new();
    private Task? _mixGenerationTask;
    private string? _mixedAudioPath;
    private string? _ffmpeg;

    private ClipEditorWindow(ClipItem item)
    {
        _item = item;
        InitializeComponent();
        FileNameText.Text = item.FileName;

        _ffmpeg = Ffmpeg.Find(Services.Settings.Current.FfmpegPath, Services.Settings.Current.LosslessCutPath);
        ExportHint.Text = _ffmpeg is null
            ? "Точный встроенный экспорт — никаких внешних программ."
            : "Быстрый экспорт: видео копируется без потери качества.";

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

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Log.Info("Editor", $"Открываю {Path.GetFileName(_item.FullPath)}");
        try
        {
            // VideoLAN.LibVLC.Windows кладёт native runtime рядом с приложением;
            // Initialize находит его сам. Загрузка сотен codec plugins бывает
            // ощутимой на первом старте, поэтому она не должна блокировать WPF.
            var created = await Task.Run(() =>
            {
                LibVLCSharp.Shared.Core.Initialize();
                var engine = new LibVLC("--no-video-title-show", "--quiet");
                var player = new VlcMediaPlayer(engine) { EnableHardwareDecoding = true };
                var media = new VlcMedia(engine, new Uri(_item.FullPath));
                return (engine, player, media);
            });

            if (_disposed)
            {
                created.media.Dispose(); created.player.Dispose(); created.engine.Dispose();
                return;
            }

            (_libVlc, _player, _media) = created;
            _player.LengthChanged += Player_LengthChanged;
            _player.EncounteredError += Player_EncounteredError;
            _player.Playing += Player_Playing;
            _player.Paused += Player_Paused;
            _player.EndReached += Player_EndReached;
            Preview.MediaPlayer = _player;

            if (!_player.Play(_media)) throw new InvalidOperationException("LibVLC не принял файл");
            _timer.Start();
            Log.Info("Editor", "LibVLC запущен, жду первый кадр");
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
        PreviewLoading.Visibility = Visibility.Collapsed;
        ConfigureAudioChoices();
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
            _mixedAudioPlayer?.Pause();
            return;
        }
        double position = CurrentSeconds;
        if (position < _startSeconds || position >= _endSeconds - 0.02) Seek(_startSeconds);
        _player.Play();
        if (_audioSelection < 0 && _mixedAudioPlayer is not null) _mixedAudioPlayer.Play();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_player is null || _durationSeconds <= 0) return;
        double position = CurrentSeconds;
        if (_player.IsPlaying && position >= _endSeconds)
        {
            _player.Pause();
            _mixedAudioPlayer?.Pause();
            Seek(_endSeconds);
            return;
        }
        long tick = Environment.TickCount64;
        if (_audioSelection < 0 && _mixedAudioPlayer?.IsPlaying == true &&
            tick - _lastAudioSyncTick >= 500)
        {
            _lastAudioSyncTick = tick;
            if (Math.Abs(_mixedAudioPlayer.Time - _player.Time) > 120)
                _mixedAudioPlayer.Time = _player.Time;
        }
        UpdatePlayhead(position);
        UpdatePreviewTime(position);
    }

    private void PreviousFrame_Click(object sender, RoutedEventArgs e) => Step(-FrameStep);
    private void NextFrame_Click(object sender, RoutedEventArgs e) => Step(FrameStep);

    private void Step(TimeSpan delta)
    {
        if (_player is null) return;
        if (_player.IsPlaying)
        {
            _player.Pause();
            _mixedAudioPlayer?.Pause();
        }
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
        if (_mixedAudioPlayer is not null) _mixedAudioPlayer.Time = _player.Time;
        UpdatePlayhead(seconds);
        UpdatePreviewTime(seconds);
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null) return;
        _isMuted = !_isMuted;
        ApplyMuteState();
    }

    private void ConfigureAudioChoices()
    {
        if (_player is null || _audioTrackIds.Length > 0) return;
        var descriptions = _player.AudioTrackDescription?.Where(track => track.Id >= 0).ToArray() ?? [];
        if (descriptions.Length == 0) return;

        _audioTrackIds = descriptions.Select(track => track.Id).ToArray();
        AudioBox.Items.Clear();
        for (int i = 0; i < _audioTrackIds.Length; i++)
        {
            string label = _audioTrackIds.Length == 1 ? "Единая аудиодорожка"
                : i == 0 ? "Только игра"
                : i == 1 ? "Только микрофон"
                : $"Только дорожка {i + 1}";
            AudioBox.Items.Add(new ComboBoxItem { Content = label, Tag = i });
        }
        if (_audioTrackIds.Length > 1)
            AudioBox.Items.Add(new ComboBoxItem { Content = "Игра + микрофон", Tag = -1 });

        AudioBox.IsEnabled = true;
        // Одна дорожка не требует второго проигрывателя и открывается мгновенно.
        // Общий микс остаётся доступен явным выбором пользователя.
        AudioBox.SelectedIndex = 0;
    }

    private async void AudioTrack_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_player is null || AudioBox.SelectedItem is not ComboBoxItem { Tag: int selected }) return;
        _audioSelection = selected;

        if (selected >= 0)
        {
            StopMixedAudio();
            if (selected < _audioTrackIds.Length) _player.SetAudioTrack(_audioTrackIds[selected]);
            return;
        }

        // LibVLC умеет проигрывать только одну встроенную дорожку за раз. Поэтому
        // ffmpeg один раз в фоне сводит звук в маленький временный M4A. Второй
        // плеер больше не открывает исходный HEVC и не может уронить видеодрайвер.
        if (_audioTrackIds.Length > 1 && _ffmpeg is not null)
        {
            AudioBox.IsEnabled = false;
            ExportStatus.Text = "Готовлю звук игры + микрофона…";
            try
            {
                await EnsureMixedAudioAsync();
                if (!_disposed && _audioSelection < 0)
                {
                    _player.SetAudioTrack(-1);
                    StartMixedAudio();
                    ExportStatus.Text = "Готово к экспорту";
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Warn("Editor", $"Предпросмотр общего звука: {ex.Message}");
                if (!_disposed)
                {
                    _player.SetAudioTrack(_audioTrackIds[0]);
                    ExportStatus.Text = "Предпросмотр общего звука недоступен";
                    ExportHint.Text = "Экспорт всё равно сведёт игру и микрофон.";
                }
            }
            finally
            {
                if (!_disposed && _exportCancellation is null) AudioBox.IsEnabled = true;
            }
        }
        else if (_audioTrackIds.Length > 1)
        {
            _player.SetAudioTrack(_audioTrackIds[0]);
            ExportStatus.Text = "В предпросмотре звучит игра";
            ExportHint.Text = "При экспорте игра и микрофон будут сведены вместе.";
        }
    }

    private Task EnsureMixedAudioAsync()
    {
        if (_ffmpeg is null || _audioTrackIds.Length < 2) return Task.CompletedTask;
        if (_mixGenerationTask is not null) return _mixGenerationTask;
        _mixedAudioPath = Path.Combine(Path.GetTempPath(), $"aura-editor-audio-{Guid.NewGuid():N}.m4a");
        _mixGenerationTask = Ffmpeg.CreateMixedAudioPreviewAsync(
            _ffmpeg, _item.FullPath, _audioTrackIds.Length, _mixedAudioPath, _previewMixCancellation.Token);
        return _mixGenerationTask;
    }

    private void StartMixedAudio()
    {
        if (_libVlc is null || _player is null || _mixedAudioPath is null || !File.Exists(_mixedAudioPath)) return;
        if (_mixedAudioPlayer is null)
        {
            _mixedAudioPlayer = new VlcMediaPlayer(_libVlc);
            _mixedAudioPlayer.Playing += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (_disposed || _mixedAudioPlayer is null || _player is null) return;
                _mixedAudioPlayer.Time = _player.Time;
                ApplyMuteState();
                if (!_player.IsPlaying) _mixedAudioPlayer.Pause();
            });
            _mixedAudioMedia = new VlcMedia(_libVlc, new Uri(_mixedAudioPath));
        }

        if (!_mixedAudioPlayer.IsPlaying && _mixedAudioMedia is not null)
        {
            var player = _mixedAudioPlayer;
            var media = _mixedAudioMedia;
            _ = Task.Run(() =>
            {
                try { if (!_disposed) player.Play(media); }
                catch (Exception ex) { Log.Warn("Editor", $"Общий звук: {ex.Message}"); }
            });
        }
    }

    private void StopMixedAudio()
    {
        _mixedAudioPlayer?.Pause();
    }

    private void ApplyMuteState()
    {
        // У LibVLC свойство Mute на части Windows-систем возвращает устаревшее
        // состояние. Громкость задаём явно и храним истину в окне редактора.
        if (_player is not null) _player.Volume = _isMuted ? 0 : 100;
        if (_mixedAudioPlayer is not null) _mixedAudioPlayer.Volume = _isMuted ? 0 : 100;
        MuteButton.Content = _isMuted ? "Звук выключен" : "Звук включён";
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

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        await ExportAsync(SmartExportAsync, fast: _ffmpeg is not null);
    }

    /// <summary>
    /// Одна кнопка, как в LosslessCut: быстрый remux при доступном ffmpeg, а если
    /// конкретный контейнер ему не подошёл — автоматический точный fallback.
    /// Никакого вопроса о выборе «режима экспорта» человеку не показываем.
    /// </summary>
    private async Task<string> SmartExportAsync(
        TimeSpan start, TimeSpan end, IProgress<double>? progress, CancellationToken ct)
    {
        if (_ffmpeg is not null)
            try
            {
                return await Ffmpeg.TrimLosslessAsync(
                    _ffmpeg, _item.FullPath, start, end, _audioSelection,
                    Math.Max(1, _audioTrackIds.Length), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn("Editor", $"Быстрый экспорт не удался, перехожу на точный: {ex.Message}");
                ExportStatus.Text = "Быстрый режим не подошёл — точный экспорт…";
            }

        return await VideoEditor.TrimPreciseAsync(
            _item.FullPath, start, end, _audioSelection, progress, ct);
    }

    private async Task ExportAsync(
        Func<TimeSpan, TimeSpan, IProgress<double>?, CancellationToken, Task<string>> export,
        bool fast)
    {
        if (_exportCancellation is not null || _durationSeconds <= 0) return;
        _player?.Pause();
        _mixedAudioPlayer?.Pause();
        _exportCancellation = new CancellationTokenSource();
        SetExporting(true, fast);
        ExportStatus.Text = "Экспортирую фрагмент…";
        ExportHint.Text = fast
            ? "Видео копируется без потери; выбранные аудиодорожки сохраняются."
            : "Встроенный точный экспорт; исходник остаётся нетронутым.";

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
            SetExporting(false, fast);
        }
    }

    private void SetExporting(bool value, bool fast)
    {
        ExportButton.IsEnabled = !value;
        AudioBox.IsEnabled = !value && _audioTrackIds.Length > 0;
        CancelExportButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        ExportProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        ExportProgress.IsIndeterminate = value && fast;
    }

    private void ShowPreviewError(string message)
    {
        PreviewLoading.Visibility = Visibility.Collapsed;
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
        _previewMixCancellation.Cancel();
        Preview.MediaPlayer = null;
        try { _player?.Stop(); } catch { }
        try { _mixedAudioPlayer?.Stop(); } catch { }
        _mixedAudioMedia?.Dispose();
        _mixedAudioPlayer?.Dispose();
        _media?.Dispose();
        _player?.Dispose();
        _libVlc?.Dispose();
        _previewMixCancellation.Dispose();
        if (_mixedAudioPath is not null)
            try { File.Delete(_mixedAudioPath); } catch { }
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
