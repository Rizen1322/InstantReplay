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
    private int[] _audioTrackIds = [];
    /// <summary>
    /// Идентификатор сведённой дорожки «игра + микрофон» внутри ОСНОВНОГО плеера.
    ///
    /// Раньше под неё запускался второй плеер LibVLC и его подгоняли к первому
    /// вызовами Time/Pause/Play из потока окна плюс таймером каждые полсекунды. Это и
    /// вешало редактор при выборе «Игра + микрофон»: вызов LibVLC из потока окна
    /// ждёт внутренний поток LibVLC, а тот, в свою очередь, ждёт очередь сообщений
    /// того же окна (VideoView — это HWND), и оба стоят. Плюс два плеера разом
    /// открывали одно звуковое устройство.
    ///
    /// Теперь сведённый M4A — дополнительная дорожка основного плеера. Подключается
    /// он к ФАЙЛУ до воспроизведения (Media.AddSlave), а не на лету
    /// (MediaPlayer.AddSlave), и разница проверена замером через перехват звука:
    /// у дорожки, добавленной на лету, перемотка на паузе уводила видео вперёд
    /// примерно на двадцать секунд. У подключённой к файлу перемотка на паузе и во
    /// время воспроизведения работает верно и со звуком.
    /// </summary>
    private int? _mixTrackId;

    /// <summary>Файл уже переоткрыт вместе со сведённым звуком.</summary>
    private bool _mixAttached;

    /// <summary>Подготовка сведённого звука и переоткрытие файла с ним — один раз.</summary>
    private Task? _mixPreparation;

    /// <summary>
    /// Куда вернуться после переоткрытия файла: позиция и играло ли видео.
    /// null — переоткрытия сейчас нет.
    /// </summary>
    private (long TimeMs, bool Playing)? _reopenRestore;

    /// <summary>
    /// Завершается, когда после переоткрытия найден номер сведённой дорожки (или
    /// искать перестали). Без него подготовка заканчивалась раньше, чем LibVLC
    /// показывал дорожку, и выбор «Вместе» видел пустой номер.
    /// </summary>
    private readonly TaskCompletionSource _mixTrackSearch =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    // -1 = свести все дорожки, 0..N = оставить одну дорожку.
    private int _audioSelection = -1;
    private double _durationSeconds;
    private double _startSeconds;
    private double _endSeconds;
    private bool _pauseOnFirstFrame = true;
    private bool _isMuted;
    private bool _disposed;
    private bool _scrubbing;
    private string? _savedPath;
    private long _fileBytes;
    private CancellationTokenSource? _exportCancellation;
    private readonly CancellationTokenSource _previewMixCancellation = new();
    private Task? _mixGenerationTask;
    private string? _mixedAudioPath;
    private string? _ffmpeg;

    private ClipEditorWindow(ClipItem item)
    {
        _item = item;
        InitializeComponent();
        _volume = Services.Settings.Current.EditorVolumePercent;
        VolumeSlider.Value = _volume;
        ShowVolume();
        FileNameText.Text = item.FileName;

        _ffmpeg = Ffmpeg.Find(Services.Settings.Current.FfmpegPath, Services.Settings.Current.LosslessCutPath);
        ExportHint.Text = DefaultHint();
        try { _fileBytes = new FileInfo(item.FullPath).Length; } catch { }

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += Timer_Tick;
        Loaded += Window_Loaded;
        Closed += (_, _) =>
        {
            DisposePlayer();
            // Громкость запоминаем при закрытии, а не на каждом шаге ползунка:
            // запись настроек на диск на каждое движение мыши ни к чему.
            if (_volume != Services.Settings.Current.EditorVolumePercent)
                Services.Settings.Update(s => s.EditorVolumePercent = _volume, "editor");
        };
    }

    /// <summary>Окно без показа: для снимка вёрстки в режиме --dev.</summary>
    internal static Window CreateForSnapshot(string path) =>
        new ClipEditorWindow(new ClipItem(path, Path.GetDirectoryName(path) ?? "")) { _snapshotOnly = true };

    /// <summary>Снимок вёрстки: плеер не поднимаем, чтобы не играл звук.</summary>
    private bool _snapshotOnly;

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
        if (_snapshotOnly) { _durationSeconds = 185; _endSeconds = 125; _startSeconds = 40; UpdatePreviewTime(74);
            _ = Dispatcher.BeginInvoke(() => { BuildRuler(); UpdateTimelineVisuals(); }, DispatcherPriority.Loaded); return; }
        Log.Info("Editor", $"Открываю {Path.GetFileName(_item.FullPath)}");
        try
        {
            // VideoLAN.LibVLC.Windows кладёт native runtime рядом с приложением;
            // Initialize находит его сам. Загрузка сотен codec plugins бывает
            // ощутимой на первом старте, поэтому она не должна блокировать WPF.
            var created = await Task.Run(() =>
            {
                LibVLCSharp.Shared.Core.Initialize();
                // Ресемплер задаём явно. Если частота устройства не 48 кГц (гарнитуры
                // часто работают на 44,1 кГц), VLC пересчитывает звук сам. В сборке
                // VideoLAN.LibVLC.Windows нет soxr, и по умолчанию берётся «ugly»:
                // искажения около −26 дБ и зеркальные частоты, звук «как у робота».
                // С speex тот же тест даёт −59 дБ, как без пересчёта.
                var engine = new LibVLC("--no-video-title-show", "--quiet",
                    "--audio-resampler=speex_resampler");
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
        // Громкость, заданная до начала воспроизведения, LibVLC может не применить
        ApplyMuteState();
        if (_reopenRestore is { } restore)
        {
            // Файл только что переоткрыт со сведённым звуком: возвращаем позицию и
            // состояние. Перемотка на паузе у подключённой к файлу дорожки безопасна.
            _reopenRestore = null;
            if (_player is null) return;
            _player.Time = restore.TimeMs;
            if (!restore.Playing) _player.SetPause(true);
            else SetPlayIcon(playing: true);
            _ = FindMixTrackAsync();
            return;
        }
        ConfigureAudioChoices();
        if (_pauseOnFirstFrame)
        {
            _pauseOnFirstFrame = false;
            _player?.SetPause(true);
            return;
        }
        SetPlayIcon(playing: true);
    });

    private void Player_Paused(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => SetPlayIcon(playing: false));

    private void Player_EndReached(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        SetPlayIcon(playing: false);
        Seek(_startSeconds);
    });

    private void SetPlayIcon(bool playing)
    {
        PlayIcon.Data = (Geometry)FindResource(playing ? "Ico.Pause" : "Ico.Play");
        PlayButton.ToolTip = playing ? "Пауза (Space)" : "Воспроизведение (Space)";
    }

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
        BuildRuler();
        UpdatePreviewTime(CurrentSeconds);
    }

    private double CurrentSeconds => _player is null ? 0 : Math.Clamp(_player.Time / 1000.0, 0, _durationSeconds);

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlayback();

    private void TogglePlayback()
    {
        if (_player is null || _durationSeconds <= 0) return;
        if (_player.IsPlaying)
        {
            _player.SetPause(true);
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
            _player.SetPause(true);
            Seek(_endSeconds);
            return;
        }
        if (_scrubbing) return;            // пока тянут шкалу, позицию ведёт мышь
        UpdatePlayhead(position);
        UpdatePreviewTime(position);
    }

    private void PreviousFrame_Click(object sender, RoutedEventArgs e) => Step(-FrameStep);
    private void NextFrame_Click(object sender, RoutedEventArgs e) => Step(FrameStep);
    private void ToStart_Click(object sender, RoutedEventArgs e) => Seek(_startSeconds);
    private void ToEnd_Click(object sender, RoutedEventArgs e) => Seek(_endSeconds);

    private void Speed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_player is null || SpeedSegment.SelectedItem is not ListBoxItem { Tag: string tag }) return;
        if (float.TryParse(tag, System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out float rate))
            _player.SetRate(rate);
    }

    private void Step(TimeSpan delta)
    {
        if (_player is null) return;
        if (_player.IsPlaying) _player.SetPause(true);
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
        ToggleMute();
    }

    /// <summary>Громкость предпросмотра, % (LibVLC принимает до 200).</summary>
    private int _volume = 100;

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _volume = (int)Math.Round(e.NewValue);
        if (VolumeValue is null) return;          // ещё идёт InitializeComponent
        ShowVolume();
        // Двинули ползунок — значит, звук хотят слышать
        if (_isMuted && _volume > 0) _isMuted = false;
        ApplyMuteState();
    }

    private void ShowVolume() => VolumeValue.Text = $"{_volume}%";

    private void StepVolume(int delta) =>
        VolumeSlider.Value = Math.Clamp(_volume + delta, VolumeSlider.Minimum, VolumeSlider.Maximum);

    private void ToggleMute()
    {
        if (_player is null) return;
        _isMuted = !_isMuted;
        ApplyMuteState();
    }

    private void ConfigureAudioChoices()
    {
        if (_player is null || _audioTrackIds.Length > 0) return;
        var descriptions = _player.AudioTrackDescription?.Where(track => track.Id >= 0).ToArray() ?? [];
        if (descriptions.Length == 0)
        {
            AudioLabel.Text = "Без звука";
            return;
        }

        _audioTrackIds = descriptions.Select(track => track.Id).ToArray();
        if (_audioTrackIds.Length == 1)
        {
            // Выбирать нечего — не показываем переключатель из одной кнопки.
            AudioLabel.Text = "Одна звуковая дорожка";
            return;
        }

        AudioSegment.Items.Clear();
        for (int i = 0; i < _audioTrackIds.Length; i++)
        {
            string label = i == 0 ? "Игра" : i == 1 ? "Микрофон" : $"Дорожка {i + 1}";
            AudioSegment.Items.Add(new ListBoxItem { Content = label, Tag = i });
        }
        var together = new ListBoxItem { Content = "Вместе", Tag = -1 };
        AudioSegment.Items.Add(together);

        AudioLabel.Visibility = Visibility.Collapsed;
        AudioSegment.Visibility = Visibility.Visible;

        // Сведённый звук готовим сразу, в фоне: к моменту, когда человек нажмёт
        // «Вместе», он уже лежит на диске, и переключение мгновенное.
        if (_ffmpeg is not null) _mixPreparation = PrepareMixAsync();

        // По умолчанию — «Вместе»: так ведёт себя и LosslessCut (в файл попадает
        // весь звук), а раньше редактор молча сохранял одну игру без микрофона.
        // Без ffmpeg предпросмотр общего звука невозможен — там остаётся игра.
        AudioSegment.SelectedItem = _ffmpeg is not null ? together : AudioSegment.Items[0];
    }

    private async void AudioTrack_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_player is null || AudioSegment.SelectedItem is not ListBoxItem { Tag: int selected }) return;
        _audioSelection = selected;
        UpdateSizeEstimate();

        if (selected >= 0)
        {
            ApplyAudioSelection();
            SetStatus("Готово к сохранению", DefaultHint());
            return;
        }

        if (_ffmpeg is null)
        {
            _player.SetAudioTrack(_audioTrackIds[0]);
            SetStatus("В предпросмотре звучит игра",
                      "В сохранённый фрагмент игра и микрофон попадут вместе.");
            return;
        }

        if (_mixTrackId is not null)
        {
            ApplyAudioSelection();
            SetStatus("Готово к сохранению", DefaultHint());
            return;
        }

        // Пока сведённый звук не готов, играет игра — окно при этом не блокируется
        // и переключатель остаётся живым: можно передумать и выбрать другую дорожку.
        _player.SetAudioTrack(_audioTrackIds[0]);
        SetStatus("Свожу игру и микрофон…", "Первый раз это занимает пару секунд.");
        try
        {
            if (_mixPreparation is not null) await _mixPreparation;
            if (_disposed || _player is null || _audioSelection >= 0) return;
            if (_mixTrackId is null) throw new InvalidOperationException("сведённая дорожка не появилась в плеере");
            ApplyAudioSelection();
            SetStatus("Готово к сохранению", DefaultHint());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("Editor", $"Предпросмотр общего звука: {ex.Message}");
            if (_disposed || _player is null) return;
            _player.SetAudioTrack(_audioTrackIds[0]);
            SetStatus("Общий звук в предпросмотре недоступен",
                      "В сохранённый фрагмент игра и микрофон всё равно попадут вместе.");
        }
    }

    /// <summary>Включить в плеере ту дорожку, что выбрана в переключателе.</summary>
    private void ApplyAudioSelection()
    {
        if (_player is null) return;
        if (_audioSelection >= 0)
        {
            if (_audioSelection < _audioTrackIds.Length) _player.SetAudioTrack(_audioTrackIds[_audioSelection]);
            return;
        }
        if (_mixTrackId is not int mixId) return;

        _player.SetAudioTrack(mixId);
        // Сведённая дорожка, выбранная ВО ВРЕМЯ воспроизведения, молчит, пока не
        // случится перемотка: LibVLC не подтягивает её к текущей позиции. Замер через
        // перехват звука — без перемотки сэмплов нет вовсе, после перемотки на то же
        // место звук идёт сразу. Плата — заминка около 0.4 с. На паузе перемотка не
        // нужна: звук появляется с ближайшим воспроизведением сам.
        if (_player.IsPlaying) _player.Time = _player.Time;
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

    /// <summary>
    /// Свести звук в фоне и переоткрыть файл уже вместе с ним.
    ///
    /// Переоткрытие нужно один раз: дорожку можно подключить к файлу только до
    /// начала воспроизведения (см. <see cref="_mixTrackId"/>). Позиция и состояние
    /// «играет / пауза» переносятся, так что для человека это короткое мигание
    /// картинки через пару секунд после открытия, а не остановка.
    /// </summary>
    private async Task PrepareMixAsync()
    {
        await EnsureMixedAudioAsync();
        if (_disposed || _player is null || _libVlc is null || _mixAttached ||
            _mixedAudioPath is null || !File.Exists(_mixedAudioPath)) return;

        _mixAttached = true;
        var media = new VlcMedia(_libVlc, new Uri(_item.FullPath));
        media.AddSlave(MediaSlaveType.Audio, 4, new Uri(_mixedAudioPath).AbsoluteUri);
        _reopenRestore = ((long)Math.Round(CurrentSeconds * 1000), _player.IsPlaying);

        // Смена файла синхронно останавливает текущий — в потоке окна это та самая
        // взаимная блокировка с видеовыводом, поэтому уходим в фон.
        var player = _player;
        var previous = _media;
        _media = media;
        await Task.Run(() => player.Play(media));
        previous?.Dispose();
        // Предел ожидания: если после переоткрытия плеер так и не начал играть
        // (ошибка декодирования), поиск дорожки не начнётся вовсе, и выбор «Вместе»
        // висел бы с надписью «свожу звук» бесконечно.
        await Task.WhenAny(_mixTrackSearch.Task, Task.Delay(TimeSpan.FromSeconds(8)));
    }

    /// <summary>
    /// Найти номер сведённой дорожки после переоткрытия: это та, которой не было
    /// среди встроенных. Список дорожек LibVLC заполняет не мгновенно, поэтому ждём.
    /// </summary>
    private async Task FindMixTrackAsync()
    {
        var known = new HashSet<int>(_audioTrackIds);
        for (int attempt = 0; attempt < 40 && !_disposed && _player is not null; attempt++)
        {
            foreach (var track in _player.AudioTrackDescription ?? [])
                if (track.Id >= 0 && !known.Contains(track.Id))
                {
                    _mixTrackId = track.Id;
                    // По умолчанию LibVLC включает подключённую дорожку сам; ставим ту,
                    // что выбрана в переключателе.
                    ApplyAudioSelection();
                    _mixTrackSearch.TrySetResult();
                    return;
                }
            await Task.Delay(75);
        }
        Log.Warn("Editor", "Сведённая дорожка не появилась после переоткрытия");
        if (!_disposed && _player is not null) ApplyAudioSelection();
        _mixTrackSearch.TrySetResult();
    }

    private string DefaultHint() => _ffmpeg is null
        ? "Точное сохранение кадр в кадр встроенными средствами, без внешних программ."
        : _audioSelection < 0 && _audioTrackIds.Length > 1
            ? "Видео копируется без потери качества, игра и микрофон сводятся в одну дорожку."
            : "Видео и звук копируются без потери качества, это быстро.";

    private void SetStatus(string status, string hint)
    {
        if (_exportCancellation is not null) return;       // во время сохранения строку не трогаем
        ExportStatus.Text = status;
        ExportHint.Text = hint;
    }

    private void ApplyMuteState()
    {
        // У LibVLC свойство Mute на части Windows-систем возвращает устаревшее
        // состояние. Громкость задаём явно и храним истину в окне редактора.
        if (_player is not null) _player.Volume = _isMuted ? 0 : _volume;
        MuteIcon.Data = (Geometry)FindResource(_isMuted ? "Ico.SpeakerOff" : "Ico.Speaker");
        MuteButton.ToolTip = _isMuted ? "Звук выключен (M)" : "Звук (M)";
    }

    private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTimelineVisuals();
        BuildRuler();
    }

    private double TimelineSeconds(double x) =>
        Math.Clamp(x / Math.Max(1, Timeline.ActualWidth), 0, 1) * _durationSeconds;

    private void Timeline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_durationSeconds <= 0 || FindParent<Thumb>(e.OriginalSource as DependencyObject) is not null) return;
        // Щелчок перематывает, а протягивание — «скраббинг», как в LosslessCut.
        _scrubbing = true;
        Timeline.CaptureMouse();
        Seek(TimelineSeconds(e.GetPosition(Timeline).X));
    }

    private void Timeline_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        Timeline.ReleaseMouseCapture();
    }

    private void Timeline_MouseMove(object sender, MouseEventArgs e)
    {
        if (_durationSeconds <= 0) return;
        double x = Math.Clamp(e.GetPosition(Timeline).X, 0, Timeline.ActualWidth);
        double seconds = TimelineSeconds(x);
        if (_scrubbing) Seek(seconds);

        HoverLine.Visibility = Visibility.Visible;
        HoverLabel.Visibility = Visibility.Visible;
        Canvas.SetLeft(HoverLine, x);
        HoverText.Text = Format(TimeSpan.FromSeconds(seconds));
        HoverLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double labelWidth = HoverLabel.DesiredSize.Width;
        Canvas.SetLeft(HoverLabel, Math.Clamp(x - labelWidth / 2, 0, Math.Max(0, Timeline.ActualWidth - labelWidth)));
    }

    private void Timeline_MouseLeave(object sender, MouseEventArgs e)
    {
        HoverLine.Visibility = Visibility.Collapsed;
        HoverLabel.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Отметки времени над шкалой. Шаг подбирается так, чтобы подписи не слипались:
    /// не чаще одной на ~90 пикселей.
    /// </summary>
    private void BuildRuler()
    {
        Ruler.Children.Clear();
        double width = Timeline.ActualWidth;
        if (width <= 0 || _durationSeconds <= 0) return;

        double[] steps = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600];
        double step = steps.FirstOrDefault(value => value / _durationSeconds * width >= 90, 600);
        var brush = (Brush)FindResource("Tx3Brush");
        for (double t = 0; t <= _durationSeconds + 1e-6; t += step)
        {
            double x = t / _durationSeconds * width;
            var tick = new System.Windows.Shapes.Rectangle { Width = 1, Height = 5, Fill = brush, Opacity = 0.7 };
            Ruler.Children.Add(tick);
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, 13);

            var time = TimeSpan.FromSeconds(t);
            var label = new TextBlock
            {
                Text = time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss")
                     : time.ToString(step < 1 ? @"m\:ss\.f" : @"m\:ss"),
                FontSize = 10.5,
                Foreground = brush
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Ruler.Children.Add(label);
            Canvas.SetLeft(label, Math.Clamp(x - label.DesiredSize.Width / 2, 0, Math.Max(0, width - label.DesiredSize.Width)));
            Canvas.SetTop(label, -1);
        }
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
        UpdateSizeEstimate();
    }

    /// <summary>
    /// Примерный размер результата. Видео копируется без перекодирования, поэтому
    /// доля от исходного файла по длительности — хорошая оценка; сведение дорожек
    /// меняет только звук, а он в клипе — единицы процентов.
    /// </summary>
    private void UpdateSizeEstimate()
    {
        if (_fileBytes <= 0 || _durationSeconds <= 0) { SizeEstimateText.Text = ""; return; }
        double share = (_endSeconds - _startSeconds) / _durationSeconds;
        SizeEstimateText.Text = $"≈ {Aura.Core.Storage.ByteSize.Format((long)(_fileBytes * share))}";
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
                ExportStatus.Text = "Быстрый режим не подошёл, делаю точный экспорт…";
            }

        return await VideoEditor.TrimPreciseAsync(
            _item.FullPath, start, end, _audioSelection, progress, ct);
    }

    private async Task ExportAsync(
        Func<TimeSpan, TimeSpan, IProgress<double>?, CancellationToken, Task<string>> export,
        bool fast)
    {
        if (_exportCancellation is not null || _durationSeconds <= 0) return;
        // SetPause(true), а не Pause(): в LibVLC Pause() — ПЕРЕКЛЮЧАТЕЛЬ. Если видео уже
        // стояло на паузе, «остановка перед сохранением» его запускала, и после
        // сохранения всё шло наоборот: иконка «воспроизвести», а видео играет, нажатие
        // Play ставит паузу — со стороны это выглядело как неработающая кнопка.
        // Поэтому во всём редакторе пауза ставится только явно.
        _player?.SetPause(true);
        ExportStatus.Text = "Сохраняю фрагмент…";
        ExportHint.Text = fast
            ? "Видео копируется без потери качества; исходный клип не меняется."
            : "Точное сохранение кадр в кадр; исходный клип не меняется.";
        _exportCancellation = new CancellationTokenSource();
        SetExporting(true, fast);
        OpenFolderButton.Visibility = Visibility.Collapsed;

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
            _savedPath = output;
            OpenFolderButton.Visibility = Visibility.Visible;
            Services.Notifications.Show(NotificationKind.Saved, "Фрагмент сохранён", Path.GetFileName(output));
        }
        catch (OperationCanceledException)
        {
            ExportStatus.Text = "Сохранение отменено";
            ExportHint.Text = "Исходный клип не изменён.";
        }
        catch (Exception ex)
        {
            Log.Error("Editor", ex);
            ExportStatus.Text = "Не удалось сохранить";
            ExportHint.Text = ex.Message;
            Dialogs.Say("Не удалось сохранить фрагмент", ex.Message);
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
        AudioSegment.IsEnabled = !value;
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

        // Остановку и освобождение LibVLC уносим из потока окна. Stop синхронный
        // (в замере — около 80 мс) и ждёт внутренние потоки LibVLC; если один из них
        // в этот момент шлёт сообщение окну видеовывода, поток окна, стоящий в Stop,
        // его не примет — и закрытие редактора подвисает. В фоне ждать некому.
        var player = _player;
        var media = _media;
        var engine = _libVlc;
        var mixPath = _mixedAudioPath;
        var mixTask = _mixGenerationTask;
        var mixCancellation = _previewMixCancellation;
        _player = null; _media = null; _libVlc = null;
        _ = Task.Run(async () =>
        {
            try { player?.Stop(); } catch { }
            media?.Dispose();
            player?.Dispose();
            engine?.Dispose();
            // ffmpeg мог ещё дописывать сведённый звук — дожидаемся отмены, иначе
            // файл удалить нельзя: он занят.
            try { if (mixTask is not null) await mixTask; } catch { }
            mixCancellation.Dispose();
            if (mixPath is not null)
                try { File.Delete(mixPath); } catch { }
        });
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_savedPath is null || !File.Exists(_savedPath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", "/select,\"" + _savedPath + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("Editor", $"Не удалось открыть папку: {ex.Message}"); }
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e) => _exportCancellation?.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // PreviewKeyDown приходит ДО сфокусированной кнопки. Обычный KeyDown она
        // поглощает сама: после клика Space повторно нажимал эту кнопку вместо
        // управления видео. Автоповтор Space тоже блокируем, иначе удержание
        // клавиши быстро переключает play/pause несколько раз.
        if (e.Key == Key.Space)
        {
            e.Handled = true;
            if (!e.IsRepeat) TogglePlayback();
        }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.I) { SetIn(); e.Handled = true; }
        else if (e.Key == Key.O) { SetOut(); e.Handled = true; }
        else if (e.Key == Key.M) { ToggleMute(); e.Handled = true; }
        else if (e.Key == Key.Up) { StepVolume(10); e.Handled = true; }
        else if (e.Key == Key.Down) { StepVolume(-10); e.Handled = true; }
        else if (e.Key == Key.Home) { Seek(_startSeconds); e.Handled = true; }
        else if (e.Key == Key.End) { Seek(_endSeconds); e.Handled = true; }
        else if (e.Key == Key.Left)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) Seek(CurrentSeconds - 1);
            else Step(-FrameStep);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) Seek(CurrentSeconds + 1);
            else Step(FrameStep);
            e.Handled = true;
        }
        else if (e.Key == Key.E && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (ExportButton.IsEnabled) Export_Click(ExportButton, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else DragMove();
    }

    private void UpdatePreviewTime(double seconds)
    {
        PreviewTime.Text = Format(TimeSpan.FromSeconds(seconds));
        DurationText.Text = $" / {Format(TimeSpan.FromSeconds(_durationSeconds))}";
    }

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
