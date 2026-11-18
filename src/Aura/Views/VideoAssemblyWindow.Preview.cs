using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Aura.Core.Library;
using Aura.Core.Logging;
using Aura.Core.Tools;
using LibVLCSharp.Shared;

namespace Aura.Views;

public partial class VideoAssemblyWindow
{
    private readonly Dictionary<string, VideoFileInfo> _sourceInfo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ClipItem> _thumbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _capturedThumbs = new(StringComparer.OrdinalIgnoreCase);


    private sealed record AssemblySnapshot(VideoSegment[] Parts, AssemblyAudioTrack[] Tracks, int Selected, int AudioTrack, double AudioSource, double Position, int AudioEvent);
    private readonly Stack<AssemblySnapshot> _assemblyHistory = new();
    private readonly Stack<AssemblySnapshot> _assemblyRedo = new();
    private readonly DispatcherTimer _assemblyTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer _assemblyFrameRefresh = new() { Interval = TimeSpan.FromMilliseconds(75) };
    private readonly SemaphoreSlim _assemblyPlayback = new(1, 1);
    private LibVLC? _assemblyVlc;
    private LibVLCSharp.Shared.MediaPlayer? _assemblyPlayer;
    private Media? _assemblyMedia;
    private int _selectionRevision, _previewIndex = -1;
    private bool _changingAssembly, _wantAssemblyPlaying, _waitingAssemblyFrame, _assemblyScrubbing;
    private double _previewStart;
    private bool _trimDragging, _trimRemembered;
    private int _assemblyLoadRevision, _loadingIndex = -1;
    private bool _assemblyOpening;
    private long _assemblyLoadStarted;
    private double? _pausedSeekTarget, _pendingSeekSource, _reverseSourceTime;
    private long _pausedSeekTick;
    private double CurrentSourceSeconds => _wantAssemblyPlaying && _assemblyPlayer is not null && !_waitingAssemblyFrame
        ? Math.Max(0, _assemblyPlayer.Time / 1000.0)
        : VideoTimeline.Locate(_segments, Timeline.Position).SourceSeconds;

    private double _reversePreviewRate;
    private double _forwardPreviewRate = 1;
    private double? _tailPosition;
    private long _tailTicks;
    private double? _gapPosition;
    private long _gapTicks;
    private double TailTime => _tailPosition.GetValueOrDefault() + (Environment.TickCount64 - _tailTicks) / 1000.0 *
        (_reversePreviewRate > 0 ? -_reversePreviewRate : _wantAssemblyPlaying ? _forwardPreviewRate : 0);
    private double GapTime => _gapPosition.GetValueOrDefault() + (Environment.TickCount64 - _gapTicks) / 1000.0 *
        (_reversePreviewRate > 0 ? -_reversePreviewRate : _wantAssemblyPlaying ? _forwardPreviewRate : 0);
    private void FreezeTail()
    { if (_tailPosition is not null) { _tailPosition = TailTime; _tailTicks = Environment.TickCount64; } }
    private void FreezeGap()
    { if (_gapPosition is not null) { _gapPosition = GapTime; _gapTicks = Environment.TickCount64; } }

    private void InitializeAssemblyPreview()
    {
        foreach (var slider in new[] { TrimStart, TrimEnd })
        {
            slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) =>
            { _trimDragging = true; _trimRemembered = false; }));
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
            {
                _trimDragging = false;
                if (Segments.SelectedItem is VideoSegment part) LoadAssemblyClip(Segments.SelectedIndex,
                    slider == TrimStart ? part.Start.TotalSeconds : Math.Max(part.Start.TotalSeconds, part.End.TotalSeconds - 0.15));
            }));
        }
        AssemblyPosition.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _assemblyScrubbing = true));
        AssemblyPosition.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        { _assemblyScrubbing = false; SeekAssembly(AssemblyPosition.Value); }));
        _assemblyTimer.Tick += AssemblyPreview_Tick;
        _assemblyFrameRefresh.Tick += (_, _) =>
        {
            _assemblyFrameRefresh.Stop();
            if (_assemblyPlayer is null || _assemblyOpening || _wantAssemblyPlaying || _reversePreviewRate > 0) return;
            _assemblyPlayer.NextFrame();
        };
        Loaded += async (_, _) =>
        {
            try
            {
                var engine = await Task.Run(() =>
                {
                    LibVLCSharp.Shared.Core.Initialize();
                    return new LibVLC("--quiet", "--no-video-title-show", "--aout=directsound", "--audio-resampler=speex_resampler", "--no-avcodec-hurry-up");
                });
                if (_lifetime.IsCancellationRequested) { engine.Dispose(); return; }
                _assemblyVlc = engine;
                _assemblyPlayer = new LibVLCSharp.Shared.MediaPlayer(engine)
                {
                    EnableKeyInput = false,
                    EnableMouseInput = false
                };
                // Mute before Play is not reliable: discard this player's audio output entirely.
                if (!_assemblyPlayer.SetAudioOutput("adummy")) throw new InvalidOperationException("Не удалось отключить звук видеоплеера.");
                _assemblyPlayer.EndReached += (_, _) => Dispatcher.BeginInvoke(() =>
                { if (_assemblyPlayer?.State == VLCState.Ended) AdvanceAssembly(); });
                _assemblyPlayer.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() =>
                { _wantAssemblyPlaying = false; Status.Text = "Не удалось показать видео. Выберите другой клип или попробуйте сохранить ролик."; });
                AssemblyPreview.MediaPlayer = _assemblyPlayer;
                _assemblyTimer.Start();
                if (Segments.SelectedIndex < 0 && _segments.Count > 0) Segments.SelectedIndex = 0;
                else await SelectAssemblyClipAsync();
                foreach (string path in _segments.Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
                {
                    if (_lifetime.IsCancellationRequested) break;
                    if (_sourceInfo.ContainsKey(path)) continue;
                    var info = await VideoAssembly.ReadInfoAsync(path, _lifetime.Token);
                    if (_lifetime.IsCancellationRequested) break;
                    _sourceInfo[path] = info;
                    ProjectItem(path).Duration = info.Duration;
                    RefreshTimeline();
                }
            }
            catch (Exception ex) { Status.Text = ex.Message; Log.Error("Editor", ex); }
        };
    }

    private async void Segments_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Timeline.SelectedIndex = Segments.SelectedIndex;
        if (!_changingAssembly)
        {
            _tailPosition = null; AssemblyPreview.Visibility = Visibility.Visible;
            await SelectAssemblyClipAsync();
        }
    }

    private async Task SelectAssemblyClipAsync(double? sourceTime = null)
    {
        int revision = ++_selectionRevision;
        if (Segments.SelectedItem is not VideoSegment part) return;
        try
        {
            if (!_sourceInfo.TryGetValue(part.Path, out var info))
                _sourceInfo[part.Path] = info = await VideoAssembly.ReadInfoAsync(part.Path, _lifetime.Token);
            if (revision != _selectionRevision || _lifetime.IsCancellationRequested) return;
            ProjectItem(part.Path).Duration = info.Duration;
            _changingAssembly = true;
            SelectedClipName.Text = part.Name;
            TrimStart.Maximum = TrimEnd.Maximum = info.Duration.TotalSeconds;
            TrimStart.Value = part.Start.TotalSeconds; TrimEnd.Value = part.End.TotalSeconds;
            ClipAudio.Items.Clear();
            ClipAudio.Items.Add(info.AudioTracks == 0 ? "Без звука" : "Все дорожки");
            for (int i = 0; i < info.AudioTracks; i++) ClipAudio.Items.Add("Дорожка " + (i + 1));
            ClipAudio.SelectedIndex = info.AudioTracks == 0 ? 0 : part.AudioTrack + 1;
            ClipAudio.IsEnabled = info.AudioTracks > 1;
            ClipAudio.Visibility = part.AudioTrack >= 0 ? Visibility.Visible : Visibility.Collapsed;
            TrimPanel.IsEnabled = _export is null;
            UpdateTrimLabels();
            _changingAssembly = false;
            RefreshTimeline();
            if (revision != _selectionRevision || _lifetime.IsCancellationRequested) return;
            LoadAssemblyClip(Segments.SelectedIndex, sourceTime ?? part.Start.TotalSeconds);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status.Text = ex.Message; Log.Error("Editor", ex); }
        finally { _changingAssembly = false; }
    }

    private async void LoadAssemblyClip(int index, double sourceTime)
    {
        if (_assemblyPlayer is null || _assemblyVlc is null || index < 0 || index >= _segments.Count || _lifetime.IsCancellationRequested) return;
        var part = _segments[index];
        bool continuous = _wantAssemblyPlaying && !_assemblyScrubbing && _tailPosition is null && index == _previewIndex + 1 &&
            Math.Abs(sourceTime - part.Start.TotalSeconds) < .01;
        long loadTicks = Environment.TickCount64;
        int revision = ++_assemblyLoadRevision;
        _loadingIndex = index;
        _pendingSeekSource = null;
        double clampedSource = Math.Clamp(sourceTime, part.Start.TotalSeconds, part.End.TotalSeconds);
        double start = Math.Clamp(clampedSource, part.Start.TotalSeconds, Math.Max(part.Start.TotalSeconds, part.End.TotalSeconds - 0.05));
        _waitingAssemblyFrame = true;
        _assemblyOpening = true;
        foreach (var audio in _audioPlayers.ToArray())
            try { if (!continuous || !_placedAudioPlayers.ContainsKey(audio.Player)) audio.Player.Mute = true; }
            catch (ObjectDisposedException) { }
        bool acquired = false;
        try
        {
            await _assemblyPlayback.WaitAsync(_lifetime.Token);
            acquired = true;
            if (_lifetime.IsCancellationRequested || revision != _assemblyLoadRevision) return;
            await StopPreviewAudioAsync(continuous);
            if (_lifetime.IsCancellationRequested || revision != _assemblyLoadRevision) return;
            var player = _assemblyPlayer;
            var vlc = _assemblyVlc;
            if (player is null || vlc is null) return;
            player.SetPause(true);
            await OpenPreviewAudioAsync(part, start, revision, index);
            if (revision != _assemblyLoadRevision || _lifetime.IsCancellationRequested) return;
            if (continuous)
            {
                clampedSource = start = Math.Min(part.End.TotalSeconds - .05, start + (Environment.TickCount64 - loadTicks) / 1000.0);
            }
            bool keepPausedInitial = !_wantAssemblyPlaying || _assemblyScrubbing || _tailPosition is not null || _gapPosition is not null;
            var old = _assemblyMedia;
            var media = new Media(vlc, new Uri(part.Path));
            media.AddOption(":start-time=" + start.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            if (keepPausedInitial) media.AddOption(":start-paused");
            _assemblyMedia = media;
            player.Mute = true;
            // Keep VLC's audio clock through its dummy output, without a second audible copy.
            // Смена файла останавливает видеовывод: ему может понадобиться свободный UI-поток.
            bool played = await Task.Run(() => player.Play(media));
            old?.Dispose();
            if (!played)
            {
                if (!ReferenceEquals(_assemblyMedia, media)) media.Dispose();
                throw new InvalidOperationException("Не удалось открыть видео для предпросмотра.");
            }
            if (_lifetime.IsCancellationRequested || revision != _assemblyLoadRevision)
            {
                // Быстрые перемотки могли уже заменить media; не оставляем
                // осиротевший нативный объект до сборки мусора.
                if (!ReferenceEquals(_assemblyMedia, media)) media.Dispose();
                return;
            }
            if (_pendingSeekSource is double pending)
            {
                _pendingSeekSource = null;
                clampedSource = Math.Clamp(pending, part.Start.TotalSeconds, part.End.TotalSeconds);
                start = Math.Clamp(clampedSource, part.Start.TotalSeconds, Math.Max(part.Start.TotalSeconds, part.End.TotalSeconds - 0.05));
                player.Time = (long)Math.Round(start * 1000);
            }
            _previewIndex = index;
            _previewStart = start;
            _assemblyLoadStarted = Environment.TickCount64;
            player.SetRate((float)_forwardPreviewRate);
            bool keepPaused = !_wantAssemblyPlaying || _assemblyScrubbing || _tailPosition is not null || _gapPosition is not null;
            if (_tailPosition is not null)
            { player.SetPause(true); _waitingAssemblyFrame = false; _tailTicks = Environment.TickCount64; }
            if (_gapPosition is not null)
            {
                player.SetPause(true); _waitingAssemblyFrame = false;
                _gapTicks = Environment.TickCount64;
                AssemblyPreview.Visibility = Visibility.Hidden;
            }
            if (keepPaused)
            {
                // Play() стартует декодер; без немедленной паузы клик по шкале
                // проигрывал первые полсекунды, а покадровая перемотка не работала.
                player.SetPause(true);
                _waitingAssemblyFrame = false;
                _pausedSeekTarget = VideoTimeline.Offset(_segments, index) + Math.Clamp(clampedSource - part.Start.TotalSeconds, 0, part.Duration.TotalSeconds);
                _pausedSeekTick = Environment.TickCount64;
                UpdateAssemblyPlayhead(_pausedSeekTarget.Value);
                if (!_assemblyFrameRefresh.IsEnabled) _assemblyFrameRefresh.Start();
            }
        }
        catch (OperationCanceledException) { if (revision == _assemblyLoadRevision) _waitingAssemblyFrame = false; }
        catch (ObjectDisposedException) { if (revision == _assemblyLoadRevision) _waitingAssemblyFrame = false; }
        catch (Exception ex) { if (revision == _assemblyLoadRevision) _waitingAssemblyFrame = false; if (!_lifetime.IsCancellationRequested) { Status.Text = ex.Message; Log.Error("Editor", ex); } }
        finally
        {
            if (revision == _assemblyLoadRevision) { _assemblyOpening = false; _loadingIndex = -1; }
            if (acquired) _assemblyPlayback.Release();
        }
    }

    private void AssemblyPreview_Tick(object? sender, EventArgs e)
    {
        if (_assemblyOpening || _assemblyPlayer is null) return;
        if (!_wantAssemblyPlaying && _reversePreviewRate == 0 && _assemblyPlayer.IsPlaying)
            _assemblyPlayer.SetPause(true);
        if (_gapPosition is not null)
        {
            double gap = Math.Max(0, GapTime);
            if (gap >= LeadingGapEnd - .001)
            {
                _gapPosition = null;
                SeekAssembly(LeadingGapEnd);
                return;
            }
            if (_previewIndex >= 0 && _previewIndex < _segments.Count)
                SyncPreviewAudio(_segments[_previewIndex].Start.TotalSeconds);
            UpdateAssemblyPlayhead(gap);
            return;
        }
        if (_previewIndex < 0 || _previewIndex >= _segments.Count) return;
        if (_tailPosition is not null)
        {
            double tail = TailTime;
            if (tail < VideoDuration - .01) { SeekAssembly(Math.Max(0, tail)); return; }
            if (tail >= ProjectDuration)
            { _tailPosition = tail = ProjectDuration; _tailTicks = Environment.TickCount64; _wantAssemblyPlaying = false; }
            SyncPreviewAudio(CurrentSourceSeconds);
            UpdateAssemblyPlayhead(tail); return;
        }
        double time = Math.Max(0, _assemblyPlayer.Time / 1000.0);
        bool alignAudio = false;
        if (_reversePreviewRate > 0 && !_assemblyScrubbing && !_waitingAssemblyFrame)
        {
            time = Math.Max(_segments[_previewIndex].Start.TotalSeconds, (_reverseSourceTime ?? time) - .08 * _reversePreviewRate);
            _reverseSourceTime = time;
            _assemblyPlayer.Time = (long)(time * 1000);
            _assemblyPlayer.NextFrame();
            if (time <= _segments[_previewIndex].Start.TotalSeconds)
            {
                if (_previewIndex > 0)
                {
                    double prevEnd = VideoTimeline.Offset(_segments, _previewIndex - 1) + _segments[_previewIndex - 1].Duration.TotalSeconds;
                    _reverseSourceTime = null;
                    SeekAssembly(Math.Max(0, prevEnd - .08));
                    return;
                }
                _reversePreviewRate = 0;
                _reverseSourceTime = null;
            }
        }
        if (_waitingAssemblyFrame)
        {
            if (_assemblyPlayer.VoutCount == 0 || time < _previewStart - 0.08)
            {
                if (Environment.TickCount64 - _assemblyLoadStarted > 8000)
                { _waitingAssemblyFrame = _wantAssemblyPlaying = false; _assemblyPlayer.SetPause(true); Status.Text = "Предпросмотр не отдал кадр. Попробуйте выбрать клип ещё раз."; }
                return;
            }
            _waitingAssemblyFrame = false;
            alignAudio = true;
            _ = CaptureAssemblyThumbnailAsync(_segments[_previewIndex].Path);
            if (!_wantAssemblyPlaying || _assemblyScrubbing) _assemblyPlayer.SetPause(true);
        }
        SyncPreviewAudio(_reversePreviewRate > 0 ? (_reverseSourceTime ?? time) : time, seek: alignAudio);
        if (!_assemblyScrubbing && _wantAssemblyPlaying && time >= _segments[_previewIndex].End.TotalSeconds - 0.08) { AdvanceAssembly(); return; }
        if (_wantAssemblyPlaying || _reversePreviewRate > 0)
        {
            double sourcePos = _reversePreviewRate > 0 && _reverseSourceTime is double rev ? rev : time;
            double global = VideoTimeline.Offset(_segments, _previewIndex) +
                Math.Clamp(sourcePos - _segments[_previewIndex].Start.TotalSeconds, 0, _segments[_previewIndex].Duration.TotalSeconds);
            if (_reversePreviewRate == 0 && _pausedSeekTarget is double seekTarget)
            {
                if (Math.Abs(global - seekTarget) > 0.25 && Environment.TickCount64 - _pausedSeekTick < 350)
                    global = seekTarget;
                else _pausedSeekTarget = null;
            }
            UpdateAssemblyPlayhead(global);
        }
    }
    private static string Clock(double seconds) =>
        $"{TimeSpan.FromSeconds(Math.Max(0, seconds)):hh\\:mm\\:ss\\.fff}";

    private void UpdateAssemblyPlayhead(double global)
    {
        if (!_assemblyScrubbing)
        {
            _changingAssembly = true;
            AssemblyPosition.Value = global;
            _changingAssembly = false;
            Timeline.Position = global;
            if (_wantAssemblyPlaying) FollowTimelinePosition(global);
            PreviewTime.Text = $"{Clock(global)} / {Clock(AssemblyPosition.Maximum)}";
        }
        PlayAssembly.Content = _wantAssemblyPlaying ? "Ⅱ Пауза" : "▶ Смотреть ролик";
    }

    private void AdvanceAssembly()
    {
        if (_lifetime.IsCancellationRequested || !_wantAssemblyPlaying || _assemblyScrubbing || _assemblyOpening || _tailPosition is not null) return;
        if (_previewIndex + 1 < _segments.Count)
        {
            if (IndependentAudioSelected && Timeline.SelectedAudioEvent >= 0)
            {
                ++_selectionRevision;
                LoadAssemblyClip(_previewIndex + 1, _segments[_previewIndex + 1].Start.TotalSeconds);
            }
            else Segments.SelectedIndex = _previewIndex + 1;
        }
        else
        {
            if (ProjectDuration > VideoDuration + .02)
            {
                _tailPosition = VideoDuration; _tailTicks = Environment.TickCount64;
                AssemblyPreview.Visibility = Visibility.Hidden;
                _assemblyPlayer?.SetPause(true); return;
            }
            _wantAssemblyPlaying = false;
            _assemblyPlayer?.SetPause(true);
            PlayAssembly.Content = "▶ Смотреть ролик";
        }
    }

    private void SeekAssembly(double time)
    {
        _reverseSourceTime = null;
        _audioRestartPositions.Clear();
        if (time < LeadingGapEnd - .001) { EnterLeadingGap(time); return; }
        _gapPosition = null;
        _tailPosition = time >= VideoDuration && ProjectDuration > VideoDuration + .02 ? Math.Clamp(time, VideoDuration, ProjectDuration) : null;
        _tailTicks = Environment.TickCount64;
        AssemblyPreview.Visibility = _tailPosition is null ? Visibility.Visible : Visibility.Hidden;
        var (index, source) = VideoTimeline.Locate(_segments, time);
        if (index < 0) return;
        Timeline.Position = Math.Clamp(time, 0, AssemblyPosition.Maximum);
        _pausedSeekTarget = Timeline.Position;
        _pausedSeekTick = Environment.TickCount64;
        bool keepAudioSelection = IndependentAudioSelected && Timeline.SelectedAudioEvent >= 0;
        _changingAssembly = true; AssemblyPosition.Value = Timeline.Position;
        if (!keepAudioSelection) Segments.SelectedIndex = index;
        _changingAssembly = false;
        if (_tailPosition is null && _gapPosition is null && index == _previewIndex &&
            _assemblyPlayer is not null && !_assemblyOpening && (_assemblyPlayer.State is VLCState.Playing or VLCState.Paused))
        {
            bool paused = !_wantAssemblyPlaying || _assemblyScrubbing;
            if (paused) _assemblyPlayer.SetPause(true);
            _assemblyPlayer.Time = (long)Math.Round(Math.Clamp(source, _segments[index].Start.TotalSeconds,
                Math.Max(_segments[index].Start.TotalSeconds, _segments[index].End.TotalSeconds)) * 1000);
            if (paused && !_assemblyFrameRefresh.IsEnabled) _assemblyFrameRefresh.Start();
            UpdateAssemblyPlayhead(Timeline.Position);
            SyncPreviewAudio(source, seek: true);
            return;
        }
        if (_tailPosition is null && _gapPosition is null && _assemblyOpening && index == _loadingIndex)
        {
            _pendingSeekSource = source;
            UpdateAssemblyPlayhead(Timeline.Position);
            SyncPreviewAudio(source, seek: true);
            return;
        }
        if (keepAudioSelection) { ++_selectionRevision; LoadAssemblyClip(index, source); }
        else _ = SelectAssemblyClipAsync(source);
    }

    private void EnterLeadingGap(double time)
    {
        _gapPosition = Math.Clamp(time, 0, LeadingGapEnd);
        _gapTicks = Environment.TickCount64;
        _tailPosition = null;
        AssemblyPreview.Visibility = Visibility.Hidden;
        _assemblyPlayer?.SetPause(true);
        bool keepAudio = IndependentAudioSelected && Timeline.SelectedAudioEvent >= 0;
        if (!keepAudio) { _changingAssembly = true; Segments.SelectedIndex = 0; _changingAssembly = false; }
        if (_previewIndex != 0 && _segments.Count > 0) _ = SelectAssemblyClipAsync(_segments[0].Start.TotalSeconds);
        UpdateAssemblyPlayhead(_gapPosition.Value);
        if (_previewIndex >= 0 && _previewIndex < _segments.Count)
            SyncPreviewAudio(_segments[_previewIndex].Start.TotalSeconds, seek: true);
    }

    private void AssemblyPosition_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (!_changingAssembly && !_assemblyScrubbing && IsLoaded) SeekAssembly(e.NewValue); }
    private void PlayAssembly_Click(object sender, RoutedEventArgs e)
    {
        if (_segments.Count == 0 || _assemblyPlayer is null) return;
        FreezeTail();
        FreezeGap();
        _pausedSeekTick = Environment.TickCount64;
        _reversePreviewRate = 0; _forwardPreviewRate = 1; _reverseSourceTime = null;
        _audioRestartPositions.Clear();
        _assemblyPlayer.SetRate(1);
        foreach (var audio in _audioPlayers) audio.Player.SetRate(1);
        _wantAssemblyPlaying = !_wantAssemblyPlaying;
        if (_wantAssemblyPlaying)
        {
            if (_gapPosition is not null) { _gapTicks = Environment.TickCount64; }
            else if (AssemblyPosition.Value < LeadingGapEnd - .001) SeekAssembly(AssemblyPosition.Value);
            else if (AssemblyPosition.Value >= AssemblyPosition.Maximum - 0.1 || _tailPosition is null && _assemblyPlayer.State == VLCState.Ended) SeekAssembly(0);
            else if (_tailPosition is null) _assemblyPlayer.SetPause(false);
        }
        else _assemblyPlayer.SetPause(true);
        PlayAssembly.Content = _wantAssemblyPlaying ? "Ⅱ Пауза" : "▶ Смотреть ролик";
        SyncPreviewAudio(CurrentSourceSeconds, seek: _wantAssemblyPlaying);
    }
    private void ToStart_Click(object sender, RoutedEventArgs e) => SeekAssembly(0);
    private void Mute_Changed(object sender, RoutedEventArgs e) => SyncPreviewAudio(CurrentSourceSeconds);

    private void RememberAssembly()
    {
        _lastAudioEdit = 0;
        _assemblyHistory.Push(CaptureAssembly()); _assemblyRedo.Clear();
        UndoAssembly.IsEnabled = true;
        RedoAssembly.IsEnabled = false;
    }
    private AssemblySnapshot CaptureAssembly() => new(_segments.ToArray(), _audioTracks.ToArray(), Segments.SelectedIndex,
        Timeline.SelectedAudioTrack, Timeline.SelectedAudioSeconds, Timeline.Position, Timeline.SelectedAudioEvent);
    private void SplitAssembly_Click(object sender, RoutedEventArgs e)
    {
        if (Timeline.SelectedAudioEvent < 0 && Timeline.SelectedAudioTrack >= 0 && Segments.SelectedItem is VideoSegment { AudioDetached: true } detachedPart)
        {
            for (int i = 0; i < detachedPart.AudioEvents.Count; i++)
            {
                var ev = detachedPart.AudioEvents[i];
                if (!ev.Removed && ev.Track == Timeline.SelectedAudioTrack && ev.Position is TimeSpan pos &&
                    Timeline.Position >= pos.TotalSeconds && Timeline.Position < (pos + ev.End - ev.Start).TotalSeconds)
                {
                    Timeline.SelectedAudioEvent = i;
                    break;
                }
            }
        }
        if (Timeline.SelectedAudioEvent >= 0 && IndependentAudioSelected && Segments.SelectedItem is VideoSegment detached)
        {
            int eventIndex = Timeline.SelectedAudioEvent;
            var audio = detached.AudioEvents[eventIndex];
            TimeSpan at = audio.Start + TimeSpan.FromSeconds(Timeline.Position) - audio.Position!.Value;
            if (at - audio.Start < TimeSpan.FromMilliseconds(50) || audio.End - at < TimeSpan.FromMilliseconds(50))
            { Status.Text = "Поставьте курсор внутри выбранного звукового фрагмента."; return; }
            RememberAssembly();
            var events = detached.AudioEvents.ToList();
            events[eventIndex] = audio with { End = at };
            events.Insert(eventIndex + 1, audio with { Start = at, Position = audio.Position + at - audio.Start });
            int owner = Segments.SelectedIndex;
            _changingAssembly = true; _segments[owner] = detached with { AudioEvents = events.ToArray() }; Segments.SelectedIndex = owner; _changingAssembly = false;
            InvalidatePlacedAudio();
            RefreshTimeline(); SeekAssembly(Timeline.Position); return;
        }
        var (index, source) = VideoTimeline.Locate(_segments, Timeline.Position);
        if (index >= 0) { Segments.SelectedIndex = index; SplitAssemblyAt(source); }
    }
    private void SplitAssemblyAt(double seconds)
    {
        if (_export is not null || Segments.SelectedItem is not VideoSegment part) return;
        if (SnapEdits.IsChecked == true && _sourceInfo.TryGetValue(part.Path, out var sourceInfo))
            seconds = Math.Round(seconds * sourceInfo.Fps) / sourceInfo.Fps;
        if (Timeline.SelectedAudioTrack >= 0 && !part.AudioLinked)
        {
            if (Timeline.SelectedAudioTrack >= SourceAudioTracks(part.Path)) return;
            var split = AssemblyAudio.Split(part, Timeline.SelectedAudioTrack, TimeSpan.FromSeconds(seconds));
            if (split == part) { Status.Text = "Поставьте курсор внутри звукового фрагмента."; return; }
            RememberAssembly();
            int selected = Segments.SelectedIndex;
            _changingAssembly = true; _segments[selected] = split; Segments.SelectedIndex = selected; _changingAssembly = false;
            Timeline.SelectedAudioSeconds = seconds;
            RefreshTimeline(); return;
        }
        var at = TimeSpan.FromSeconds(seconds);
        if (at - part.Start < TimeSpan.FromMilliseconds(50) || part.End - at < TimeSpan.FromMilliseconds(50))
        { Status.Text = "Поставьте позицию просмотра внутри клипа, чтобы разрезать его."; return; }
        RememberAssembly();
        int index = Segments.SelectedIndex;
        _wantAssemblyPlaying = false;
        _changingAssembly = true;
        var splitVideo = AssemblyAudio.SplitVideo(part, at, VideoTimeline.Offset(_segments, index));
        _segments[index] = splitVideo.Left;
        _segments.Insert(index + 1, splitVideo.Right);
        _changingAssembly = false;
        Segments.SelectedIndex = index + 1;
    }
    private void UndoAssembly_Click(object sender, RoutedEventArgs e)
    {
        if (_export is not null || !_assemblyHistory.TryPop(out var previous)) return;
        _assemblyRedo.Push(CaptureAssembly());
        RestoreAssembly(previous);
    }
    private void RedoAssembly_Click(object sender, RoutedEventArgs e)
    {
        if (_export is not null || !_assemblyRedo.TryPop(out var next)) return;
        _assemblyHistory.Push(CaptureAssembly());
        RestoreAssembly(next);
    }
    private void RestoreAssembly(AssemblySnapshot snapshot)
    {
        _lastAudioEdit = 0;
        ++_selectionRevision;
        InvalidatePlacedAudio();
        _wantAssemblyPlaying = false; _reversePreviewRate = 0; _reverseSourceTime = null; _assemblyPlayer?.SetPause(true);
        _changingAssembly = true;
        _audioTracks.Clear(); _audioTracks.AddRange(snapshot.Tracks);
        _segments.Clear(); foreach (var part in snapshot.Parts) _segments.Add(part);
        Timeline.SelectedAudioTrack = snapshot.AudioTrack; Timeline.SelectedAudioSeconds = snapshot.AudioSource;
        Timeline.SelectedAudioEvent = snapshot.AudioEvent;
        if (snapshot.AudioTrack >= 0) _activeAudioTrack = snapshot.AudioTrack;
        Segments.SelectedIndex = Math.Clamp(snapshot.Selected, -1, _segments.Count - 1);
        _changingAssembly = false;
        UndoAssembly.IsEnabled = _assemblyHistory.Count > 0;
        RedoAssembly.IsEnabled = _assemblyRedo.Count > 0;
        if (_segments.Count > 0) { RefreshTimeline(); SeekAssembly(Math.Clamp(snapshot.Position, 0, AssemblyPosition.Maximum)); }
        if (_segments.Count == 0)
        {
            _wantAssemblyPlaying = false; _assemblyPlayer?.SetPause(true);
            TrimPanel.IsEnabled = false; Timeline.Position = 0; AssemblyPosition.Value = 0;
        }
    }
    private void Trim_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_changingAssembly || _export is not null || Segments.SelectedItem is not VideoSegment part) return;
        double start = TrimStart.Value, end = TrimEnd.Value;
        if (end - start < 0.05)
        {
            _changingAssembly = true;
            if (sender == TrimStart) TrimStart.Value = part.Start.TotalSeconds; else TrimEnd.Value = part.End.TotalSeconds;
            _changingAssembly = false; return;
        }
        ReplaceAssemblyPart(part with { Start = TimeSpan.FromSeconds(start), End = TimeSpan.FromSeconds(end) });
        _wantAssemblyPlaying = false;
        if (!_trimDragging) LoadAssemblyClip(Segments.SelectedIndex, sender == TrimStart ? start : Math.Max(start, end - 0.15));
        UpdateTrimLabels();
    }
    private void ReplaceAssemblyPart(VideoSegment part)
    {
        int index = Segments.SelectedIndex;
        if (!_trimDragging || !_trimRemembered) { RememberAssembly(); _trimRemembered = true; }
        _changingAssembly = true; _segments[index] = part; Segments.SelectedIndex = index; _changingAssembly = false;
    }
    private void UpdateTrimLabels()
    {
        TrimStartTime.Text = TimeSpan.FromSeconds(TrimStart.Value).ToString(@"hh\:mm\:ss\.fff");
        TrimEndTime.Text = TimeSpan.FromSeconds(TrimEnd.Value).ToString(@"hh\:mm\:ss\.fff");
    }
    private void MarkStart_Click(object sender, RoutedEventArgs e) { if (_assemblyPlayer is not null) TrimStart.Value = Math.Max(0, _assemblyPlayer.Time / 1000.0); }
    private void MarkEnd_Click(object sender, RoutedEventArgs e) { if (_assemblyPlayer is not null) TrimEnd.Value = Math.Max(0, _assemblyPlayer.Time / 1000.0); }
    private void ClipAudio_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_changingAssembly || _export is not null || Segments.SelectedItem is not VideoSegment part) return;
        ReplaceAssemblyPart(part with { AudioTrack = ClipAudio.SelectedIndex - 1 });
        _ = SelectAssemblyClipAsync();
        Timeline.Focus();
    }
    private async void Thumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not VideoSegment part) return;
        try
        {
            if (!_thumbs.TryGetValue(part.Path, out var item))
            {
                _thumbs[part.Path] = item = new ClipItem(part.Path, Path.GetDirectoryName(part.Path)!);
                await ClipThumbnails.LoadAsync(item);
            }
            if (!_lifetime.IsCancellationRequested && image.DataContext is VideoSegment current && current.Path == part.Path)
                image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(ClipItem.Thumbnail)) { Source = item });
        }
        catch (Exception ex) { Log.Warn("Editor", "Миниатюра: " + ex.Message); }
    }
    private async Task CaptureAssemblyThumbnailAsync(string path)
    {
        if (_assemblyPlayer is not { } player || !_thumbs.TryGetValue(path, out var item) || item.Thumbnail is not null || !_capturedThumbs.Add(path)) return;
        string dir = Path.Combine(Path.GetTempPath(), "Aura", "thumbs");
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, "thumb-" + Guid.NewGuid().ToString("N") + ".png");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool finished = false;
        void Taken(object? sender, MediaPlayerSnapshotTakenEventArgs args)
        {
            if (args.Filename == temp)
            {
                if (finished) try { File.Delete(temp); } catch { }
                else ready.TrySetResult();
            }
        }
        player.SnapshotTaken += Taken;
        try
        {
            if (!player.TakeSnapshot(0, temp, 320, 0)) return;
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(3), _lifetime.Token);
            using var stream = File.OpenRead(temp);
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); item.Thumbnail = bitmap;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or NotSupportedException)
        { }
        finally
        {
            finished = true;
            try { player.SnapshotTaken -= Taken; } catch (ObjectDisposedException) { }
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private void CloseAssemblyPreview()
    {
        _assemblyTimer.Stop();
        _assemblyFrameRefresh.Stop();
        AssemblyPreview.MediaPlayer = null;
        foreach (var item in _thumbs.Values) item.PropertyChanged -= OnThumbPropertyChanged;
        var player = _assemblyPlayer; var media = _assemblyMedia; var engine = _assemblyVlc;
        _assemblyPlayer = null;
        _assemblyMedia = null;
        _assemblyVlc = null;

        _ = Task.Run(async () =>
        {
            await _assemblyPlayback.WaitAsync();
            try { await StopPreviewAudioAsync(); player?.Stop(); player?.Dispose(); media?.Dispose(); engine?.Dispose(); }
            finally { _assemblyPlayback.Release(); }
        });
    }
}
